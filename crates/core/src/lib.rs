//! ShareLink LAN protocol v1: TLS 1.3, explicit acceptance and bounded streaming.
//! Discovery is only a hint. Each send pins the receiver certificate and requires
//! explicit receiver acceptance before file content is transferred.
use anyhow::{anyhow, bail, ensure, Context, Result};
use rustls::{
    client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier},
    pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer, ServerName, UnixTime},
    ClientConfig, ClientConnection, DigitallySignedStruct, ServerConfig, ServerConnection,
    SignatureScheme, StreamOwned,
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::{HashMap, HashSet},
    ffi::{c_char, CStr, CString},
    fs::{self, File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    net::{IpAddr, Ipv4Addr, Shutdown, SocketAddr, TcpListener, TcpStream, UdpSocket},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
        Arc, Condvar, Mutex, OnceLock,
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const CHUNK: usize = 1024 * 1024;
const MAX_FILES: usize = 1000;
const MAX_SESSIONS: usize = 8;
const HISTORY: usize = 256;
const ACCEPT_TIMEOUT: Duration = Duration::from_secs(120);
mod link;
mod nearby;
mod transport;
mod browser;
mod personal_hotspot;

#[derive(Clone, Deserialize)]
pub struct Config {
    pub name: String,
    #[serde(default = "unknown_device_type")]
    pub device_type: String,
    pub download_dir: PathBuf,
    pub state_dir: PathBuf,
    #[serde(default = "default_port")]
    pub port: u16,
    #[serde(default = "default_discovery_port")]
    pub discovery_port: u16,
    #[serde(default)]
    pub nearby_only: bool,
    #[serde(default)]
    pub transport_modules: Option<Vec<String>>,
    #[serde(default)]
    pub streaming_hash: bool,
    #[serde(default)]
    pub discard_partials: bool,
    #[serde(default)]
    pub partial_dir: Option<PathBuf>,
}
fn unknown_device_type() -> String { "unknown".into() }
fn normalized_device_type(value: &str) -> String {
    match value { "phone" => "phone", "tablet" => "tablet", "computer" | "2in1" | "pc" | "desktop" | "laptop" => "computer", _ => "unknown" }.into()
}
fn default_port() -> u16 {
    53317
}
fn default_discovery_port() -> u16 {
    53318
}

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Info {
    #[serde(default = "unknown_device_type")]
    device_type: String,
    id: String,
    name: String,
    port: u16,
    addresses: Vec<String>,
    fingerprint: String,
    verification_code: String,
    #[serde(default)]
    link_report: Value,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Peer {
    #[serde(default = "unknown_device_type")]
    device_type: String,
    id: String,
    name: String,
    host: String,
    port: u16,
    fingerprint: String,
    verification_code: String,
    last_seen: u64,
    #[serde(default)]
    addresses: Vec<String>,
    #[serde(default)]
    link_report: Value,
}
#[derive(Clone, Serialize, Deserialize)]
struct Entry {
    name: String,
    size: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    path: Option<String>,
}
#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Transfer {
    id: String,
    direction: String,
    peer_name: String,
    #[serde(default = "unknown_device_type")]
    peer_device_type: String,
    files: Vec<Entry>,
    state: String,
    bytes: u64,
    total: u64,
    speed: u64,
    #[serde(default)]
    created_at: u64,
    #[serde(default)]
    finished_at: u64,
    #[serde(default)]
    stage_durations_ms: HashMap<String, u64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
}
#[derive(Clone, Serialize, Deserialize)]
struct CancelCapability {
    token: String,
    port: u16,
    fingerprint: String,
}
#[derive(Clone)]
struct CancelPeer {
    host: String,
    capability: CancelCapability,
}
struct Job {
    view: Mutex<Transfer>,
    cancelled: AtomicBool,
    transport_failed: AtomicBool,
    cancel_token: String,
    cancel_peer: Mutex<Option<CancelPeer>>,
    decision: Mutex<Option<bool>>,
    changed: Condvar,
    socket: Mutex<Option<TcpStream>>,
    began: Instant,
    state_began: Mutex<Instant>,
    done: AtomicBool,
    initial_bytes: AtomicU64,
    sample: Mutex<(Instant, u64)>,
}
impl Job {
    fn new(direction: &str, peer: &str, files: Vec<Entry>, state: &str) -> Arc<Self> {
        Arc::new(Self {
            view: Mutex::new(Transfer {
                id: uuid::Uuid::new_v4().to_string(),
                direction: direction.into(),
                peer_name: peer.into(),
                peer_device_type: unknown_device_type(),
                total: files.iter().fold(0u64, |total, file| total.saturating_add(file.size)),
                files,
                state: state.into(),
                bytes: 0,
                speed: 0,
                created_at: epoch(),
                finished_at: 0,
                stage_durations_ms: HashMap::new(),
                error: None,
            }),
            cancelled: AtomicBool::new(false),
            transport_failed: AtomicBool::new(false),
            cancel_token: format!("{}{}", uuid::Uuid::new_v4().simple(), uuid::Uuid::new_v4().simple()),
            cancel_peer: Mutex::new(None),
            decision: Mutex::new(None),
            changed: Condvar::new(),
            socket: Mutex::new(None),
            began: Instant::now(),
            state_began: Mutex::new(Instant::now()),
            done: AtomicBool::new(false),
            initial_bytes: AtomicU64::new(0),
            sample: Mutex::new((Instant::now(), 0)),
        })
    }
    fn set_peer_device_type(&self, value: &str) { self.view.lock().unwrap().peer_device_type = normalized_device_type(value); }
    fn id(&self) -> String {
        self.view.lock().unwrap().id.clone()
    }
    fn state(&self, state: &str) {
        let mut v = self.view.lock().unwrap();
        if v.state != "cancelled" {
            if v.state != state {
                let mut began = self.state_began.lock().unwrap();
                let previous = v.state.clone();
                *v.stage_durations_ms.entry(previous).or_default() += began.elapsed().as_millis() as u64;
                *began = Instant::now();
                // Each file verifies separately, but the rate covers the whole batch.
                if state == "transferring" && v.state != "verifying" {
                    let payload_bytes = v.bytes.saturating_sub(self.initial_bytes.load(Ordering::Relaxed));
                    *self.sample.lock().unwrap() = (Instant::now(), payload_bytes);
                    v.speed = 0;
                }
            }
            v.state = state.into();
        }
    }
    fn progress(&self, bytes: u64) {
        let mut v = self.view.lock().unwrap();
        v.bytes = bytes;
        if v.state != "transferring" {
            return;
        }
        let payload_bytes = bytes.saturating_sub(self.initial_bytes.load(Ordering::Relaxed));
        let mut sample = self.sample.lock().unwrap();
        let now = Instant::now();
        if payload_bytes < sample.1 {
            *sample = (now, payload_bytes);
            v.speed = 0;
            return;
        }
        let elapsed = now.duration_since(sample.0);
        let additional_bytes = payload_bytes - sample.1;
        if elapsed.is_zero() || additional_bytes == 0 {
            return;
        }
        let sample_due = elapsed >= Duration::from_millis(500);
        if sample_due || v.speed == 0 {
            v.speed = (additional_bytes as f64 / elapsed.as_secs_f64()) as u64;
        }
        if sample_due {
            *sample = (now, payload_bytes);
        }
    }
    fn check(&self) -> Result<()> {
        ensure!(
            !self.cancelled.load(Ordering::Relaxed),
            "Transfer cancelled"
        );
        Ok(())
    }
    fn attach(&self, socket: &TcpStream) -> Result<()> {
        *self.socket.lock().unwrap() = Some(socket.try_clone()?);
        self.check()
    }
    fn set_cancel_peer(&self, host: String, capability: CancelCapability) -> Result<()> {
        ensure!(valid_hash(&capability.token) && valid_hash(&capability.fingerprint) && capability.port != 0,
            "Invalid cancellation capability");
        *self.cancel_peer.lock().unwrap() = Some(CancelPeer { host, capability });
        self.changed.notify_all();
        Ok(())
    }
    fn cancel(&self) {
        self.cancelled.store(true, Ordering::Relaxed);
        self.state("cancelled");
        if let Some(s) = self.socket.lock().unwrap().as_ref() {
            let _ = s.shutdown(Shutdown::Both);
        }
        self.changed.notify_all();
    }
    fn finish(&self, result: Result<()>) {
        if let Err(error) = result {
            let mut v = self.view.lock().unwrap();
            if v.state != "cancelled" {
                if v.state != "rejected" { v.state = "failed".into(); }
                self.transport_failed.store(is_transport_error(&error), Ordering::Relaxed);
                v.error = Some(format!("{error:#}"));
            }
        } else {
            self.state("completed");
        }
        { let mut v = self.view.lock().unwrap(); v.finished_at = epoch(); v.speed = 0; v.stage_durations_ms.insert("total".into(), self.began.elapsed().as_millis() as u64); }
        self.socket.lock().unwrap().take();
    }
}

pub struct Core {
    transport: transport::TransportPolicy,
    config: Config,
    info: Info,
    device_name: Mutex<String>,
    server: Arc<ServerConfig>,
    stopped: AtomicBool,
    peers: Mutex<HashMap<String, Peer>>,
    jobs: Mutex<Vec<Arc<Job>>>,
    receiving: AtomicUsize,
    partials: Mutex<HashSet<String>>,
    discovery: Option<UdpSocket>,
    link_report: Mutex<Value>,
    measuring: AtomicUsize,
    pairings: Mutex<nearby::Pairings>,
    pulls: Mutex<HashMap<String, PendingPull>>,
    history_lock: Mutex<()>,
    browser: Mutex<Option<browser::Server>>,
}
struct PendingPull {
    inputs: Vec<Source>,
    job: Arc<Job>,
    expires: Instant,
}

impl Core {
    pub fn start(mut config: Config) -> Result<Arc<Self>> {
        ensure!(
            !config.name.trim().is_empty()
                && config.name.len() <= 128
                && !config.name.chars().any(char::is_control),
            "Invalid device name"
        );
        fs::create_dir_all(&config.download_dir)?;
        fs::create_dir_all(&config.state_dir)?;
        config.download_dir = fs::canonicalize(&config.download_dir)?;
        config.state_dir = fs::canonicalize(&config.state_dir)?;
        let partial_dir = config.partial_dir.clone().unwrap_or_else(|| config.download_dir.join(".sharelink-partials"));
        if partial_dir.exists() { ensure!(!fs::symlink_metadata(&partial_dir)?.file_type().is_symlink(), "Unsafe partial directory"); }
        config.partial_dir = Some(partial_dir);
        if config.discard_partials { clean_owned_partials(config.partial_dir.as_ref().unwrap())?; }
        // Upgrade cleanup: discard legacy owned staging, while retaining every complete received file.
        if config.discard_partials { clean_owned_partials(&config.download_dir.join(".sharelink-partials"))?; }
        let history_path = config.state_dir.join("transfer-history.json");
        let mut previous: Vec<Transfer> = if history_path.exists() {
            serde_json::from_slice(&fs::read(&history_path)?).context("Cannot read transfer history")?
        } else { Vec::new() };
        previous.truncate(HISTORY);
        let user_exit_marker = config.state_dir.join("user-exit.pending");
        let user_terminated = user_exit_marker.is_file();
        let restored: Vec<Arc<Job>> = previous.into_iter().map(|mut view| {
            if !terminal(&view.state) {
                view.state = if user_terminated { "cancelled" } else { "failed" }.into();
                view.error = Some(if user_terminated { "已被用户终止" } else { "上次任务已随应用退出终止，请重新发送" }.into());
                view.finished_at = epoch();
            }
            view.speed = 0;
            let job = Job::new(&view.direction, &view.peer_name, view.files.clone(), &view.state);
            job.set_peer_device_type(&view.peer_device_type);
            *job.view.lock().unwrap() = view;
            job.done.store(true, Ordering::Relaxed);
            job
        }).collect();
        let (cert, key) = identity(&config.state_dir)?;
        let fingerprint = hex::encode(Sha256::digest(&cert));
        let provider = Arc::new(rustls::crypto::ring::default_provider());
        let server = ServerConfig::builder_with_provider(provider)
            .with_protocol_versions(&[&rustls::version::TLS13])?
            .with_no_client_auth()
            .with_single_cert(
                vec![CertificateDer::from(cert)],
                PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key)),
            )?;
        let listener = TcpListener::bind((Ipv4Addr::UNSPECIFIED, config.port))
            .context("Cannot listen on transfer port")?;
        listener.set_nonblocking(true)?;
        let port = listener.local_addr()?.port();
        let info = Info {
            device_type: normalized_device_type(&config.device_type),
            id: fingerprint[..32].into(),
            name: config.name.clone(),
            port,
            addresses: local_addresses(),
            verification_code: fingerprint[..16].to_uppercase(),
            fingerprint,
            link_report: Value::Null,
        };
        let transport = transport::TransportPolicy::new(&config)?;
        let discovery = if config.discovery_port == 0 || !transport.lan_enabled {
            None
        } else {
            Some(discovery_socket(config.discovery_port)?)
        };
        let core = Arc::new(Self {
            transport,
            device_name: Mutex::new(config.name.clone()),
            config,
            info,
            server: Arc::new(server),
            stopped: AtomicBool::new(false),
            peers: Mutex::new(HashMap::new()),
            jobs: Mutex::new(restored),
            receiving: AtomicUsize::new(0),
            partials: Mutex::new(HashSet::new()),
            discovery,
            link_report: Mutex::new(Value::Null),
            measuring: AtomicUsize::new(0),
            pairings: Mutex::new(nearby::Pairings::default()),
            pulls: Mutex::new(HashMap::new()),
            history_lock: Mutex::new(()),
            browser: Mutex::new(None),
        });
        core.persist_history()?;
        if user_terminated { fs::remove_file(user_exit_marker)?; }
        let worker = core.clone();
        thread::spawn(move || worker.listen(listener));
        if core.discovery.is_some() {
            let worker = core.clone();
            thread::spawn(move || worker.discovery_loop());
        }
        Ok(core)
    }
    pub fn stop(&self) { let _ = self.stop_with_reason(false); }
    fn stop_with_reason(&self, user_terminated: bool) -> Result<()> {
        self.stopped.store(true, Ordering::Relaxed);
        if let Some(mut browser) = self.browser.lock().unwrap().take() { let _ = browser.stop(); }
        for j in self.jobs.lock().unwrap().iter() {
            if !terminal(&j.view.lock().unwrap().state) {
                j.cancel();
                if user_terminated {
                    let mut view = j.view.lock().unwrap();
                    view.error = Some("已被用户终止".into()); view.finished_at = epoch(); view.speed = 0;
                }
            }
        }
        let initial_history = self.persist_history();
        for (_, pull) in self.pulls.lock().unwrap().drain() {
            pull.job.finish(Err(anyhow!("Application exited")));
            pull.job.done.store(true, Ordering::Release);
        }
        let deadline = Instant::now() + Duration::from_secs(2);
        loop {
            let pending = self.jobs.lock().unwrap().iter().any(|j| !j.done.load(Ordering::Acquire));
            if !pending || Instant::now() >= deadline { break; }
            thread::sleep(Duration::from_millis(5));
        }
        let final_history = self.persist_history();
        let cleanup = if user_terminated && self.config.discard_partials { clean_owned_partials(self.config.partial_dir.as_ref().unwrap()) } else { Ok(()) };
        initial_history.and(final_history).and(cleanup)
    }
    fn persist_history(&self) -> Result<()> {
        let _guard = self.history_lock.lock().unwrap();
        let views: Vec<Transfer> = self.jobs.lock().unwrap().iter().map(|j| j.view.lock().unwrap().clone()).collect();
        let path = self.config.state_dir.join("transfer-history.json");
        let temporary = self.config.state_dir.join("transfer-history.tmp");
        let mut file = File::create(&temporary)?;
        file.write_all(&serde_json::to_vec(&views)?)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, &path)?;
        Ok(())
    }
    fn finish_job(&self, job: &Arc<Job>, result: Result<()>) {
        // Keep the task active briefly after a transport error: the separate
        // authenticated cancel connection can arrive after the data socket closes.
        // This also prevents the UI from releasing direct Wi-Fi before it arrives.
        if result.as_ref().err().map(is_transport_error).unwrap_or(false)
            && job.cancel_peer.lock().unwrap().is_some() {
            let until = Instant::now() + Duration::from_secs(2);
            let mut decision = job.decision.lock().unwrap();
            while !job.cancelled.load(Ordering::Relaxed) && Instant::now() < until {
                decision = job.changed.wait_timeout(decision, until.saturating_duration_since(Instant::now())).unwrap().0;
            }
        }
        job.finish(result);
        if let Err(error) = self.persist_history() { eprintln!("History persistence failed: {error}"); }
        // Shutdown must wait for durable history, not just the in-memory terminal state.
        job.done.store(true, Ordering::Release);
    }
    fn cancel_capability(&self, job: &Job) -> CancelCapability {
        CancelCapability { token: job.cancel_token.clone(), port: self.info.port, fingerprint: self.info.fingerprint.clone() }
    }
    fn notify_cancel(&self, job: &Job) -> bool {
        let peer = job.cancel_peer.lock().unwrap().clone();
        if let Some(peer) = peer {
            return (|| -> Result<()> {
                let mut stream = self.connect(&peer.host, peer.capability.port, Some(peer.capability.fingerprint))?;
                write_json(&mut stream, &json!({"op":"cancel_transfer","token":peer.capability.token}))?;
                check_response(read_json(&mut stream)?)?;
                Ok(())
            })().is_ok();
        }
        false
    }
    fn source_address(&self) -> Result<Option<Ipv4Addr>> {
        self.transport.source()
    }
    fn connect(&self, host: &str, port: u16, pin: Option<String>) -> Result<StreamOwned<ClientConnection, TcpStream>> {
        connect_from(host, port, pin, self.source_address()?)
    }
    fn named_info(&self) -> Info {
        let mut info = self.info.clone();
        info.name = self.device_name.lock().unwrap().clone();
        info
    }
    fn current_info(&self) -> Info {
        let mut info = self.named_info();
        info.addresses = self.transport.addresses();
        info.link_report = self.link_report.lock().unwrap().clone();
        info
    }
    pub fn call(self: &Arc<Self>, request: Value) -> Value {
        match self.command(request) {
            Ok(value) => value,
            Err(error) => json!({"ok":false,"error":format!("{error:#}")}),
        }
    }
    fn command(self: &Arc<Self>, r: Value) -> Result<Value> {
        ensure!(!self.stopped.load(Ordering::Relaxed), "Core is stopped");
        match r["op"].as_str().unwrap_or("") {
            "terminate_user" => {
                let mut marker = File::create(self.config.state_dir.join("user-exit.pending"))?;
                marker.write_all(b"user exit\n")?; marker.sync_all()?;
                self.stop_with_reason(true)?;
                Ok(json!({"ok":true}))
            }
            "set_name" => {
                let name = field(&r, "name")?;
                ensure!(!name.trim().is_empty() && name.len() <= 128 && !name.chars().any(char::is_control), "Invalid device name");
                *self.device_name.lock().unwrap() = name.into();
                Ok(json!({"ok":true}))
            }
            "lan_pause" => {
                self.transport.paused.store(r["paused"].as_bool().unwrap_or(true), Ordering::Release);
                self.peers.lock().unwrap().clear(); Ok(json!({"ok":true}))
            }
            "personal_hotspot_address" => Ok(json!({"ok":true,"address":personal_hotspot::address(r.get("personalHotspotContext"))?.to_string()})),
            "browser_start" => {
                ensure!(!self.jobs.lock().unwrap().iter().any(|job| !terminal(&job.view.lock().unwrap().state)), "End active transfers before changing mode");
                let mut slot = self.browser.lock().unwrap(); ensure!(slot.is_none(), "Browser sharing already active");
                let address:Ipv4Addr = field(&r,"address")?.parse()?;
                if r["personalHotspot"].as_bool().unwrap_or(false) {
                    ensure!(personal_hotspot::address(r.get("personalHotspotContext"))? == address, "Personal hotspot address changed before browser start");
                }
                let files:Vec<Source> = serde_json::from_value(r["files"].clone())?;
                let port = u16::try_from(r["port"].as_u64().unwrap_or(80))?;
                let max_receivers = r["maxReceivers"].as_u64().map(usize::try_from).transpose()?;
                let server = browser::Server::start_limited(address,port,files,max_receivers)?;
                let snapshot = server.snapshot(); *slot=Some(server); Ok(json!({"ok":true,"browser":snapshot}))
            }
            "browser_allow_upload" => {
                let slot = self.browser.lock().unwrap();
                let server = slot.as_ref().ok_or_else(|| anyhow!("Browser sharing is not active"))?;
                server.set_upload_allowed(&self.config.download_dir, r["enabled"].as_bool().unwrap_or(true), r["sessionId"].as_str())?;
                Ok(json!({"ok":true,"browser":server.snapshot()}))
            }
            "browser_add" => {
                let slot = self.browser.lock().unwrap();
                let server = slot.as_ref().ok_or_else(|| anyhow!("Browser sharing is not active"))?;
                let files:Vec<Source> = serde_json::from_value(r["files"].clone())?;
                server.append(files)?;
                Ok(json!({"ok":true,"browser":server.snapshot()}))
            }
            "browser_remove" => {
                let slot = self.browser.lock().unwrap();
                let server = slot.as_ref().ok_or_else(|| anyhow!("Browser sharing is not active"))?;
                let source: Source = serde_json::from_value(r["file"].clone())?;
                server.remove(&source)?;
                Ok(json!({"ok":true,"browser":server.snapshot()}))
            }
            "browser_snapshot" => Ok(json!({"ok":true,"browser":self.browser.lock().unwrap().as_ref().map(|server| server.snapshot())})),
            "browser_stop" => {
                let mut slot = self.browser.lock().unwrap();
                if let Some(server) = slot.as_mut() {
                    ensure!(r["force"].as_bool().unwrap_or(false) || server.snapshot()["activeDownloads"].as_u64()==Some(0),"Downloads are active");
                    server.stop()?;
                }
                *slot=None; Ok(json!({"ok":true}))
            }
            "direct_bind" => {
                let address = if r["address"].is_null() { None } else {
                    let ip: Ipv4Addr = field(&r, "address")?.parse()?;
                    Some(ip)
                };
                self.transport.bind(address)?;
                if address.is_none() {
                    for job in self.jobs.lock().unwrap().iter() {
                        if !terminal(&job.view.lock().unwrap().state) { job.cancel(); }
                    }
                }
                Ok(json!({"ok":true}))
            }
            "report_link" => {
                ensure!(r["report"].is_object(), "Link report must be an object");
                ensure!(
                    serde_json::to_vec(&r["report"])?.len() <= 16 * 1024
                        && r["report"].as_object().unwrap().len() <= 64,
                    "Link report too large"
                );
                *self.link_report.lock().unwrap() = r["report"].clone();
                Ok(json!({"ok":true}))
            }
            "measure" => {
                let _guard = CountGuard::acquire(&self.measuring, 1)?;
                link::measure(&r, self.source_address()?)
            }
            "select_path" => link::select_path(&r),
            "pair_begin" | "pair_finish" | "pair_confirm" | "pair_encrypt" | "pair_decrypt"
            | "pair_close" => self.pairings.lock().unwrap().call(&r, &self.current_info()),
            "snapshot" => {
                let now = epoch();
                let mut peers = self.peers.lock().unwrap();
                peers.retain(|_, p| now.saturating_sub(p.last_seen) < 15);
                let jobs: Vec<Transfer> = self
                    .jobs
                    .lock()
                    .unwrap()
                    .iter()
                    .map(|j| j.view.lock().unwrap().clone())
                    .collect();
                Ok(
                    json!({"ok":true,"info":self.current_info(),"peers":peers.values().collect::<Vec<_>>(),"transfers":jobs}),
                )
            }
            "clear_history" => {
                self.jobs.lock().unwrap().retain(|j| !terminal(&j.view.lock().unwrap().state));
                self.persist_history()?;
                Ok(json!({"ok":true}))
            }
            "delete_history" => {
                let id = field(&r, "id")?;
                {
                    let mut jobs = self.jobs.lock().unwrap();
                    let index = jobs.iter().position(|job| job.id() == id)
                        .ok_or_else(|| anyhow!("Transfer not found"))?;
                    ensure!(terminal(&jobs[index].view.lock().unwrap().state), "Cannot delete an active transfer");
                    jobs.remove(index);
                }
                self.persist_history()?;
                Ok(json!({"ok":true}))
            }
            "discover" => {
                self.announce(true);
                Ok(json!({"ok":true}))
            }
            "probe" => {
                let host = field(&r, "host")?;
                let port = port_field(&r)?;
                let mut stream = self.connect(&host, port, None)?;
                write_json(&mut stream, &json!({"op":"hello"}))?;
                let info: Info = serde_json::from_value(read_json(&mut stream)?)?;
                let cert = stream
                    .conn
                    .peer_certificates()
                    .and_then(|x| x.first())
                    .ok_or_else(|| anyhow!("Missing peer certificate"))?;
                let fingerprint = hex::encode(Sha256::digest(cert.as_ref()));
                ensure!(
                    info.fingerprint == fingerprint,
                    "Peer identity does not match certificate"
                );
                let peer = Peer {
                    device_type: normalized_device_type(&info.device_type),
                    id: info.id.clone(),
                    name: info.name.clone(),
                    host,
                    port,
                    fingerprint: fingerprint.clone(),
                    verification_code: fingerprint[..16].to_uppercase(),
                    last_seen: epoch(),
                    addresses: info.addresses.clone(),
                    link_report: info.link_report.clone(),
                };
                Ok(json!({"ok":true,"peer":peer,"info":info}))
            }
            "prepare_pull" => {
                let inputs: Vec<Source> = serde_json::from_value(r["files"].clone())?;
                let files = source_entries(&inputs)?;
                let job = Job::new(
                    "send",
                    r["peerName"].as_str().unwrap_or("Nearby device"),
                    files,
                    "waiting_connection",
                );
                job.set_peer_device_type(r["peerDeviceType"].as_str().unwrap_or("unknown"));
                let mut pulls = self.pulls.lock().unwrap();
                pulls.retain(|_, pull| {
                    if Instant::now() >= pull.expires {
                        pull.job.finish(Err(anyhow!("Pull request expired")));
                    pull.job.done.store(true, Ordering::Release);
                        false
                    } else {
                        !terminal(&pull.job.view.lock().unwrap().state)
                    }
                });
                ensure!(pulls.len() < MAX_SESSIONS, "Too many pending nearby sends");
                self.add_job(job.clone())?;
                let token = nearby::random_token()?;
                let id = job.id();
                pulls.insert(
                    token.clone(),
                    PendingPull {
                        inputs,
                        job,
                        expires: Instant::now() + Duration::from_secs(180),
                    },
                );
                Ok(json!({"ok":true,"id":id,"token":token,"expiresInSeconds":180}))
            }
            "pull" => {
                self.source_address()?;
                let host = field(&r["peer"], "host")?;
                let port = port_field(&r["peer"])?;
                let pin = verify_peer(&r)?;
                let token = field(&r, "token")?;
                ensure!(valid_hash(&token), "Invalid pull token");
                let job = Job::new(
                    "receive",
                    r["peer"]["name"].as_str().unwrap_or(&host),
                    Vec::new(),
                    "connecting",
                );
                job.set_peer_device_type(r["peer"]["deviceType"].as_str().unwrap_or("unknown"));
                self.add_job(job.clone())?;
                let id = job.id();
                let core = self.clone();
                thread::spawn(move || {
                    let result = core.pull_files(&job, &host, port, &pin, &token);
                    core.finish_job(&job, result);
                });
                Ok(json!({"ok":true,"id":id}))
            }
            "send" => {
                self.source_address()?;
                let peer = &r["peer"];
                let host = field(peer, "host")?;
                let port = port_field(peer)?;
                let fingerprint = verify_peer(&r)?;
                let inputs: Vec<Source> = serde_json::from_value(r["files"].clone())?;
                let files = source_entries(&inputs)?;
                let job = Job::new(
                    "send",
                    peer["name"].as_str().unwrap_or(&host),
                    files,
                    "hashing",
                );
                job.set_peer_device_type(peer["deviceType"].as_str().unwrap_or("unknown"));
                self.add_job(job.clone())?;
                let id = job.id();
                let core = self.clone();
                thread::spawn(move || {
                    let result = core.send_files(&job, &host, port, &fingerprint, inputs);
                    core.finish_job(&job, result);
                });
                Ok(json!({"ok":true,"id":id}))
            }
            "accept" | "reject" | "cancel" => {
                let id = field(&r, "id")?;
                let job = self
                    .jobs
                    .lock()
                    .unwrap()
                    .iter()
                    .find(|j| j.id() == id)
                    .cloned()
                    .ok_or_else(|| anyhow!("Transfer not found"))?;
                let op = r["op"].as_str().unwrap();
                if op == "cancel" {
                    ensure!(
                        !terminal(&job.view.lock().unwrap().state),
                        "Transfer already finished"
                    );
                    // A pending offer may be visible before its capability reply is consumed.
                    // Let that bounded handshake finish before closing the socket it uses.
                    if job.view.lock().unwrap().state == "awaiting_accept" {
                        let until = Instant::now() + Duration::from_secs(1);
                        let mut decision = job.decision.lock().unwrap();
                        while job.cancel_peer.lock().unwrap().is_none() && !job.done.load(Ordering::Acquire) && Instant::now() < until {
                            decision = job.changed.wait_timeout(decision, until.saturating_duration_since(Instant::now())).unwrap().0;
                        }
                    }
                    ensure!(!terminal(&job.view.lock().unwrap().state), "Transfer already finished");
                    job.cancel();
                    let notified = self.notify_cancel(&job);
                    return Ok(json!({"ok":true,"cancelNotified":notified}));
                } else {
                    let view = job.view.lock().unwrap();
                    ensure!(
                        view.direction == "receive" && view.state == "awaiting_accept",
                        "Transfer is not awaiting acceptance"
                    );
                    drop(view);
                    let mut decision = job.decision.lock().unwrap();
                    ensure!(decision.is_none(), "Request already answered");
                    *decision = Some(op == "accept");
                    if op == "reject" {
                        job.state("rejected");
                        let mut view = job.view.lock().unwrap(); view.error = Some("Request declined".into()); view.finished_at = epoch();
                    }
                    job.changed.notify_all();
                }
                Ok(json!({"ok":true}))
            }
            _ => bail!("Unknown command"),
        }
    }
    fn add_job(&self, job: Arc<Job>) -> Result<()> {
        let mut jobs = self.jobs.lock().unwrap();
        // stop() holds the same lock while cancelling jobs, so an accepted socket
        // cannot create a new transfer after the core has been stopped.
        ensure!(!self.stopped.load(Ordering::Relaxed), "Core is stopped");
        ensure!(
            jobs.iter()
                .filter(|j| !terminal(&j.view.lock().unwrap().state))
                .count()
                < MAX_SESSIONS,
            "Too many active transfers"
        );
        if jobs.len() >= HISTORY {
            if let Some(i) = jobs
                .iter()
                .position(|j| terminal(&j.view.lock().unwrap().state))
            {
                jobs.remove(i);
            }
        }
        jobs.push(job);
        drop(jobs);
        self.persist_history()?;
        Ok(())
    }
    fn listen(self: Arc<Self>, listener: TcpListener) {
        while !self.stopped.load(Ordering::Relaxed) {
            self.pulls.lock().unwrap().retain(|_, pull| {
                if Instant::now() >= pull.expires {
                    pull.job.finish(Err(anyhow!("Pull request expired")));
                    pull.job.done.store(true, Ordering::Release);
                    false
                } else {
                    !terminal(&pull.job.view.lock().unwrap().state)
                }
            });
            match listener.accept() {
                Ok((socket, _)) => {
                    if self.receiving.fetch_add(1, Ordering::Relaxed) >= MAX_SESSIONS + 2 {
                        self.receiving.fetch_sub(1, Ordering::Relaxed);
                        continue;
                    }
                    let core = self.clone();
                    thread::spawn(move || {
                        if let Err(error) = core.incoming(socket) {
                            #[cfg(test)]
                            eprintln!("Incoming connection: {error:#}");
                            #[cfg(not(test))]
                            let _ = error;
                        }
                        core.receiving.fetch_sub(1, Ordering::Relaxed);
                    });
                }
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    thread::sleep(Duration::from_millis(50))
                }
                Err(_) => break,
            }
        }
    }
    fn incoming(&self, socket: TcpStream) -> Result<()> {
        self.transport.admit(socket.local_addr()?.ip())?;
        // Winsock inherits the listener's nonblocking mode on accepted sockets.
        // The bounded worker uses blocking I/O with explicit timeouts.
        socket.set_nonblocking(false)?;
        socket.set_nodelay(true)?;
        socket.set_read_timeout(Some(Duration::from_secs(10)))?;
        socket.set_write_timeout(Some(Duration::from_secs(10)))?;
        let mut stream = StreamOwned::new(ServerConnection::new(self.server.clone())?, socket);
        let r = read_json(&mut stream)?;
        if r["op"] == "cancel_transfer" {
            let token = field(&r, "token")?;
            ensure!(valid_hash(&token), "Invalid cancellation token");
            let job = self.jobs.lock().unwrap().iter().find(|j| j.cancel_token == token).cloned()
                .ok_or_else(|| anyhow!("Unknown cancellation token"))?;
            // Hold the state lock through the decision so a finished task cannot be rewritten.
            {
                let mut view = job.view.lock().unwrap();
                // Closing the data socket may beat this separate TLS notification.
                // Only an explicit secret capability may resolve a recent transport failure as cancelled.
                let raced_disconnect = view.state == "failed" && job.transport_failed.load(Ordering::Relaxed)
                    && epoch().saturating_sub(view.finished_at) <= 5;
                ensure!(!terminal(&view.state) || view.state == "cancelled" || raced_disconnect, "Transfer already finished");
                job.cancelled.store(true, Ordering::Relaxed);
                view.state = "cancelled".into();
                if view.error.as_deref() != Some("已被用户终止") { view.error = None; }
            }
            job.cancel();
            self.persist_history()?;
            return write_json(&mut stream, &json!({"ok":true}));
        }
        if r["op"] == "hello" {
            return write_json(&mut stream, &serde_json::to_value(self.current_info())?);
        }
        if r["op"] == "measure" && r["version"] == 1 {
            let _guard = CountGuard::acquire(&self.measuring, 1)?;
            return link::serve_measure(&mut stream, &r);
        }
        if r["op"] == "pull" && r["version"] == 1 {
            let token = field(&r, "token")?;
            ensure!(valid_hash(&token), "Invalid pull token");
            let pull = self
                .pulls
                .lock()
                .unwrap()
                .remove(&token)
                .ok_or_else(|| anyhow!("Pull token missing, expired or already used"))?;
            let result = (|| {
                ensure!(Instant::now() < pull.expires, "Pull token expired");
                pull.job.attach(&stream.sock)?;
                if let Some(capability) = r.get("cancel") {
                    pull.job.set_cancel_peer(stream.sock.peer_addr()?.ip().to_string(), serde_json::from_value(capability.clone())?)?;
                }
                stream
                    .sock
                    .set_read_timeout(Some(Duration::from_secs(130)))?;
                let metadata = self.prepare_metadata(&pull.job, &pull.inputs, Some(&mut stream))?;
                let socket = stream.sock.try_clone()?;
                self.send_on_stream(&pull.job, &mut stream, &socket, metadata, pull.inputs)
            })();
            if let Err(e) = &result {
                let _ = write_json(&mut stream, &json!({"ok":false,"error":format!("{e:#}")}));
            }
            self.finish_job(&pull.job, result);
            return Ok(());
        }
        ensure!(
            r["op"] == "offer" && (r["version"] == 1 || r["version"] == 2),
            "Unsupported protocol"
        );
        let offer: Offer = serde_json::from_value(r)?;
        validate_offer(&offer)?;
        let files = offer
            .files
            .iter()
            .map(|f| Entry {
                name: f.name.clone(),
                size: f.size,
                path: None,
            })
            .collect();
        let job = Job::new("receive", &offer.sender, files, "awaiting_accept");
        job.set_peer_device_type(&offer.sender_device_type);
        job.attach(&stream.sock)?;
        self.add_job(job.clone())?;
        let socket = stream.sock.try_clone()?;
        let result = self.receive_files(&job, &mut stream, &socket, offer);
        if let Err(e) = &result {
            let _ = write_json(&mut stream, &json!({"ok":false,"rejected":job.view.lock().unwrap().state == "rejected","error":format!("{e:#}")}));
        }
        self.finish_job(&job, result);
        Ok(())
    }
    fn receive_files<S: Read + Write>(
        &self,
        job: &Arc<Job>,
        stream: &mut S,
        socket: &TcpStream,
        offer: Offer,
    ) -> Result<()> {
        if let Some(capability) = &offer.cancel {
            job.set_cancel_peer(socket.peer_addr()?.ip().to_string(), capability.clone())?;
            // Only upgraded senders receive this optional pre-acceptance control frame.
            write_json(stream, &json!({"ok":true,"pending":true,"cancel":self.cancel_capability(job)}))?;
        }
        let deadline = Instant::now() + ACCEPT_TIMEOUT;
        socket.set_read_timeout(Some(Duration::from_millis(50)))
            .context("Configure acceptance connection check timeout")?;
        loop {
            job.check()?;
            let decision = job.decision.lock().unwrap();
            if let Some(accepted) = *decision {
                if !accepted { job.state("rejected"); bail!("Request declined"); }
                break;
            }
            ensure!(Instant::now() < deadline, "Request expired");
            let (decision, _) = job
                .changed
                .wait_timeout(decision, Duration::from_millis(200))
                .unwrap();
            drop(decision);
            let mut byte = [0];
            match socket.peek(&mut byte) {
                Ok(0) => return Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "Sender disconnected before acceptance").into()),
                Err(e)
                    if !matches!(
                        e.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                    ) =>
                {
                    return Err(e).context("Check sender connection before acceptance")
                }
                _ => {}
            }
        }
        socket.set_read_timeout(Some(Duration::from_secs(30)))?;
        write_json(stream, &json!({"ok":true,"accepted":true,"receiverDeviceType":self.info.device_type,"streamingHash":offer.streaming_hash}))?;
        job.state("transferring");
        let partial_dir = self.config.partial_dir.clone().unwrap();
        fs::create_dir_all(&partial_dir)?;
        ensure!(
            !fs::symlink_metadata(&partial_dir)?.file_type().is_symlink(),
            "Unsafe partial directory"
        );
        let mut completed = 0u64;
        for (index, meta) in offer.files.iter().enumerate() {
            job.check()?;
            let key = if self.config.discard_partials || offer.streaming_hash {
                format!("{}-{index}", job.id())
            } else { format!("{}-{}", meta.hash, meta.size) };
            let _guard = PartialGuard::acquire(self, &key)?;
            let path = partial_dir.join(format!("{key}.part"));
            let _cleanup = IncompleteCleanup { path: path.clone(), enabled: self.config.discard_partials || offer.streaming_hash };
            let mut file = open_partial(&path)?;
            let mut offset = file.metadata()?.len();
            if offset > meta.size {
                file.set_len(0)?;
                offset = 0;
            }
            let prefix = hash_with_heartbeat(&mut file, offset, job, stream)?;
            write_json(
                stream,
                &json!({"ok":true,"offset":offset,"prefixHash":prefix}),
            )?;
            let ready = read_control(stream, job)?;
            let requested = ready["offset"]
                .as_u64()
                .ok_or_else(|| anyhow!("Invalid resume response"))?;
            ensure!(
                requested == offset || requested == 0,
                "Invalid resume offset"
            );
            if requested == 0 {
                file.set_len(0)?;
            }
            offset = requested;
            file.seek(SeekFrom::Start(offset))?;
            job.initial_bytes.fetch_add(offset, Ordering::Relaxed);
            job.progress(completed + offset);
            let mut streaming_digest = blake3::Hasher::new();
            let mut remaining = meta.size - offset;
            let mut buf = vec![0u8; CHUNK];
            while remaining > 0 {
                job.check()?;
                let take = remaining.min(CHUNK as u64) as usize;
                stream
                    .read_exact(&mut buf[..take])
                    .context("Connection ended before the complete file arrived")?;
                file.write_all(&buf[..take])?;
                if offer.streaming_hash { streaming_digest.update(&buf[..take]); }
                remaining -= take as u64;
                job.progress(completed + meta.size - remaining);
            }
            file.sync_all()?;
            job.state("verifying");
            file.seek(SeekFrom::Start(0))?;
            let (hash, expected) = if offer.streaming_hash {
                let digest = read_control(stream, job)?;
                let expected = field(&digest, "hash")?;
                ensure!(valid_hash(&expected), "Invalid streaming digest");
                (streaming_digest.finalize().to_hex().to_string(), expected)
            } else { (hash_with_heartbeat(&mut file, meta.size, job, stream)?, meta.hash.clone()) };
            if hash != expected {
                drop(file);
                fs::remove_file(&path)?;
                bail!("File integrity check failed");
            }
            job.check()?;
            drop(file);
            let destination = publish_no_replace(&path, &self.config.download_dir, &meta.name)?;
            {
                let mut view = job.view.lock().unwrap();
                view.files[index].path = Some(destination.to_string_lossy().into_owned());
                view.files[index].name = destination
                    .file_name()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned();
            }
            self.persist_history()?;
            completed += meta.size;
            job.progress(completed);
            job.state("transferring");
            write_json(stream, &json!({"ok":true,"saved":true}))?;
        }
        write_json(stream, &json!({"ok":true,"complete":true}))?;
        Ok(())
    }
    fn send_files(
        &self,
        job: &Arc<Job>,
        host: &str,
        port: u16,
        pin: &str,
        inputs: Vec<Source>,
    ) -> Result<()> {
        let metadata = self.prepare_metadata::<std::io::Sink>(job, &inputs, None)?;
        job.check()?;
        let mut stream = self.connect(host, port, Some(pin.to_string()))?;
        job.attach(&stream.sock)?;
        stream
            .sock
            .set_read_timeout(Some(Duration::from_secs(130)))?;
        let socket = stream.sock.try_clone()?;
        self.send_on_stream(job, &mut stream, &socket, metadata, inputs)
    }
    fn prepare_metadata<W: Write>(
        &self,
        job: &Arc<Job>,
        inputs: &[Source],
        mut stream: Option<&mut W>,
    ) -> Result<Vec<Meta>> {
        job.state("hashing");
        let mut metadata = Vec::new();
        for input in inputs {
            job.check()?;
            let mut file = input.open()?;
            let size = file.metadata()?.len();
            let hash = if self.config.streaming_hash { String::new() } else if let Some(ref mut stream) = stream {
                hash_with_heartbeat(&mut file, size, job, stream)?
            } else {
                hash_reader(&mut file, size, Some(job))?
            };
            metadata.push(Meta {
                name: input.name.clone(),
                size,
                hash,
            });
        }
        {
            let mut v = job.view.lock().unwrap();
            v.total = checked_total_size(metadata.iter().map(|m| m.size))?;
            for (i, m) in metadata.iter().enumerate() {
                v.files[i].size = m.size;
            }
        }
        Ok(metadata)
    }
    fn send_on_stream<S: Read + Write>(
        &self,
        job: &Arc<Job>,
        stream: &mut S,
        socket: &TcpStream,
        metadata: Vec<Meta>,
        inputs: Vec<Source>,
    ) -> Result<()> {
        job.check()?;
        write_json(
            stream,
            &json!({"op":"offer","version":if self.config.streaming_hash { 2 } else { 1 },"streamingHash":self.config.streaming_hash,"sender":self.current_info().name,"senderDeviceType":self.info.device_type,"files":metadata,"cancel":self.cancel_capability(job)}),
        )?;
        job.state("awaiting_accept");
        let acceptance = loop {
            let response = read_control(stream, job)?;
            if let Some(capability) = response.get("cancel") {
                job.set_cancel_peer(socket.peer_addr()?.ip().to_string(), serde_json::from_value(capability.clone())?)?;
            }
            if response["ok"] != true || response["pending"] != true { break response; }
        };
        if acceptance["rejected"] == true {
            job.view.lock().unwrap().error = Some(acceptance["error"].as_str().unwrap_or("Request declined").into());
            job.state("rejected");
        }
        let acceptance = check_response(acceptance)?;
        if let Some(kind) = acceptance["receiverDeviceType"].as_str() { job.set_peer_device_type(kind); }
        ensure!(
            acceptance["accepted"] == true,
            "Missing receiver acceptance"
        );
        ensure!(!self.config.streaming_hash || acceptance["streamingHash"] == true, "Receiver does not support streaming integrity");
        job.check()?;
        socket.set_read_timeout(Some(Duration::from_secs(30)))?;
        job.state("transferring");
        let mut completed = 0;
        let mut buf = vec![0u8; CHUNK];
        for (meta, input) in metadata.iter().zip(inputs.iter()) {
            job.check()?;
            let mut file = input.open()?;
            ensure!(
                file.metadata()?.is_file() && file.metadata()?.len() == meta.size,
                "Source file changed during transfer"
            );
            let ready = check_response(read_control(stream, job)?)?;
            let offset = ready["offset"]
                .as_u64()
                .ok_or_else(|| anyhow!("Missing resume offset"))?;
            ensure!(offset <= meta.size, "Peer requested invalid offset");
            file.seek(SeekFrom::Start(0))?;
            let prefix = hash_with_heartbeat(&mut file, offset, job, stream)?;
            let offset = if ready["prefixHash"].as_str() == Some(&prefix) {
                offset
            } else {
                0
            };
            write_json(stream, &json!({"offset":offset}))?;
            file.seek(SeekFrom::Start(offset))?;
            job.initial_bytes.fetch_add(offset, Ordering::Relaxed);
            let source_modified = file.metadata()?.modified().ok();
            let mut streaming_digest = blake3::Hasher::new();
            let mut remaining = meta.size - offset;
            job.progress(completed + offset);
            while remaining > 0 {
                job.check()?;
                let take = remaining.min(CHUNK as u64) as usize;
                file.read_exact(&mut buf[..take])
                    .context("Source file changed during transfer")?;
                stream.write_all(&buf[..take])?;
                if self.config.streaming_hash { streaming_digest.update(&buf[..take]); }
                remaining -= take as u64;
                job.progress(completed + meta.size - remaining);
            }
            stream.flush()?;
            if self.config.streaming_hash {
                ensure!(file.metadata()?.len() == meta.size && file.metadata()?.modified().ok() == source_modified, "Source file changed during transfer");
                write_json(stream, &json!({"hash":streaming_digest.finalize().to_hex().to_string()}))?;
            }
            job.state("verifying");
            let saved = check_response(read_control(stream, job)?)?;
            ensure!(
                saved["saved"] == true,
                "Missing durable file acknowledgement"
            );
            completed += meta.size;
            job.state("transferring");
        }
        let response = check_response(read_json(stream)?)?;
        ensure!(
            response["complete"] == true,
            "Missing final acknowledgement"
        );
        job.progress(completed);
        Ok(())
    }
    fn pull_files(
        &self,
        job: &Arc<Job>,
        host: &str,
        port: u16,
        pin: &str,
        token: &str,
    ) -> Result<()> {
        let mut stream = self.connect(host, port, Some(pin.to_string()))?;
        job.attach(&stream.sock)?;
        stream
            .sock
            .set_read_timeout(Some(Duration::from_secs(130)))?;
        write_json(&mut stream, &json!({"op":"pull","version":1,"token":token,"cancel":self.cancel_capability(job)}))?;
        let value = read_control(&mut stream, job)?;
        ensure!(
            value["op"] == "offer" && (value["version"] == 1 || value["version"] == 2),
            "Invalid pull offer: {}",
            value["error"].as_str().unwrap_or("Unsupported response")
        );
        let offer: Offer = serde_json::from_value(value)?;
        validate_offer(&offer)?;
        {
            let mut view = job.view.lock().unwrap();
            view.peer_name = offer.sender.clone();
            view.peer_device_type = normalized_device_type(&offer.sender_device_type);
            view.files = offer
                .files
                .iter()
                .map(|m| Entry {
                    name: m.name.clone(),
                    size: m.size,
                    path: None,
                })
                .collect();
            view.total = checked_total_size(offer.files.iter().map(|m| m.size))?;
            ensure!(!job.cancelled.load(Ordering::Relaxed), "Transfer cancelled");
            view.state = "awaiting_accept".into();
        }
        let socket = stream.sock.try_clone()?;
        let result = self.receive_files(job, &mut stream, &socket, offer);
        if let Err(e) = &result {
            let _ = write_json(&mut stream, &json!({"ok":false,"rejected":job.view.lock().unwrap().state == "rejected","error":format!("{e:#}")}));
        }
        result
    }
    fn announce(&self, probe: bool) {
        if self.transport.paused.load(Ordering::Acquire) { return; }
        if let Some(socket) = &self.discovery {
            let value = json!({"magic":"sharelink-v1","probe":probe,"info":self.named_info()});
            if let Ok(bytes) = serde_json::to_vec(&value) {
                let _ = socket.send_to(&bytes, (Ipv4Addr::BROADCAST, self.config.discovery_port));
                if let Ok(interfaces) = if_addrs::get_if_addrs() {
                    for i in interfaces {
                        if let if_addrs::IfAddr::V4(v) = i.addr {
                            if let Some(b) = v.broadcast {
                                let _ = socket.send_to(&bytes, (b, self.config.discovery_port));
                            }
                        }
                    }
                }
            }
        }
    }
    fn discovery_loop(&self) {
        let socket = self.discovery.as_ref().unwrap();
        let mut next = Instant::now();
        let mut buf = [0u8; 4096];
        while !self.stopped.load(Ordering::Relaxed) {
            if self.transport.paused.load(Ordering::Acquire) { thread::sleep(Duration::from_millis(100)); continue; }
            if Instant::now() >= next {
                self.announce(false);
                next = Instant::now() + Duration::from_secs(3);
            }
            if let Ok((n, source)) = socket.recv_from(&mut buf) {
                if let Ok(v) = serde_json::from_slice::<Value>(&buf[..n]) {
                    if v["magic"] != "sharelink-v1" {
                        continue;
                    }
                    if let Ok(info) = serde_json::from_value::<Info>(v["info"].clone()) {
                        if info.id == self.info.id
                            || !valid_hash(&info.fingerprint)
                            || info.name.len() > 128
                            || info.name.chars().any(char::is_control)
                            || info.port == 0
                        {
                            continue;
                        }
                        let peer = Peer {
                    device_type: normalized_device_type(&info.device_type),
                            id: info.fingerprint[..32].into(),
                            name: info.name,
                            host: source.ip().to_string(),
                            port: info.port,
                            verification_code: info.fingerprint[..16].to_uppercase(),
                            fingerprint: info.fingerprint,
                            last_seen: epoch(),
                            addresses: info.addresses,
                            link_report: Value::Null,
                        };
                        let mut peers = self.peers.lock().unwrap();
                        peers.retain(|_, p| epoch().saturating_sub(p.last_seen) < 15);
                        if peers.len() < 256 || peers.contains_key(&peer.id) {
                            peers.insert(peer.id.clone(), peer);
                        }
                        drop(peers);
                        if v["probe"] == true {
                            let bytes = serde_json::to_vec(
                                &json!({"magic":"sharelink-v1","probe":false,"info":self.named_info()}),
                            )
                            .unwrap();
                            let _ = socket.send_to(&bytes, source);
                        }
                    }
                }
            }
        }
    }
}

#[derive(Deserialize)]
struct Source {
    #[serde(default)]
    path: PathBuf,
    #[serde(default)]
    fd: Option<i32>,
    name: String,
}
impl Source {
    fn open(&self) -> Result<File> {
        #[cfg(not(unix))]
        ensure!(self.fd.is_none(), "File descriptors are not supported on this platform");
        #[cfg(unix)]
        if let Some(fd) = self.fd {
            use std::os::fd::FromRawFd;
            ensure!(fd >= 0, "Invalid source descriptor");
            let duplicated = unsafe { libc::dup(fd) };
            ensure!(duplicated >= 0, "Cannot retain source descriptor");
            let mut file = unsafe { File::from_raw_fd(duplicated) };
            file.seek(SeekFrom::Start(0))?;
            return Ok(file);
        }
        Ok(File::open(&self.path)?)
    }
}
fn checked_total_size(mut sizes: impl Iterator<Item = u64>) -> Result<u64> {
    sizes.try_fold(0u64, |total, size| total.checked_add(size).ok_or_else(|| anyhow!("Combined file size cannot be represented")))
}
fn source_entries(inputs: &[Source]) -> Result<Vec<Entry>> {
    ensure!(
        !inputs.is_empty() && inputs.len() <= MAX_FILES,
        "Choose 1 to 1000 files"
    );
    let entries = inputs
        .iter()
        .map(|f| {
            safe_name(&f.name)?;
            let metadata = f.open()?.metadata().context("Cannot read source file")?;
            ensure!(
                metadata.is_file(),
                "Source must be an ordinary file"
            );
            Ok(Entry {
                name: f.name.clone(),
                size: metadata.len(),
                path: None,
            })
        })
        .collect::<Result<Vec<_>>>()?;
    checked_total_size(entries.iter().map(|file| file.size))?;
    Ok(entries)
}
fn verify_peer(r: &Value) -> Result<String> {
    let fingerprint = field(&r["peer"], "fingerprint")?.to_lowercase();
    ensure!(valid_hash(&fingerprint), "Invalid receiver fingerprint");
    let code = field(r, "verificationCode")?
        .chars()
        .filter(|c| !c.is_whitespace() && *c != '-')
        .collect::<String>()
        .to_lowercase();
    ensure!(
        code.len() == 16 && code == fingerprint[..16],
        "Verification code does not match receiving device"
    );
    Ok(fingerprint)
}
struct CountGuard<'a>(&'a AtomicUsize);
impl<'a> CountGuard<'a> {
    fn acquire(counter: &'a AtomicUsize, maximum: usize) -> Result<Self> {
        counter
            .try_update(Ordering::SeqCst, Ordering::SeqCst, |count| {
                if count < maximum {
                    Some(count + 1)
                } else {
                    None
                }
            })
            .map_err(|_| anyhow!("Measurement already busy"))?;
        Ok(Self(counter))
    }
}
impl Drop for CountGuard<'_> {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::SeqCst);
    }
}
#[derive(Serialize, Deserialize)]
struct Meta {
    name: String,
    size: u64,
    hash: String,
}
#[derive(Deserialize)]
struct Offer {
    #[serde(default)]
    cancel: Option<CancelCapability>,
    #[serde(default, rename = "streamingHash")]
    streaming_hash: bool,
    sender: String,
    #[serde(default = "unknown_device_type", rename = "senderDeviceType")]
    sender_device_type: String,
    files: Vec<Meta>,
}
fn validate_offer(offer: &Offer) -> Result<()> {
    ensure!(
        !offer.sender.is_empty()
            && offer.sender.len() <= 128
            && !offer.sender.chars().any(char::is_control),
        "Invalid sender name"
    );
    ensure!(
        !offer.files.is_empty() && offer.files.len() <= MAX_FILES,
        "Invalid file count"
    );
    for m in &offer.files {
        safe_name(&m.name)?;
        ensure!(
            valid_hash(&m.hash) || (offer.streaming_hash && m.hash.is_empty()),
            "Invalid file metadata"
        );
    }
    checked_total_size(offer.files.iter().map(|file| file.size))?;
    Ok(())
}
fn is_transport_error(error: &anyhow::Error) -> bool {
    error.chain().any(|cause| cause.downcast_ref::<std::io::Error>()
        .map(|e| matches!(e.kind(), std::io::ErrorKind::ConnectionReset | std::io::ErrorKind::ConnectionAborted
            | std::io::ErrorKind::BrokenPipe | std::io::ErrorKind::UnexpectedEof | std::io::ErrorKind::NotConnected
            | std::io::ErrorKind::TimedOut)).unwrap_or(false))
}
fn terminal(s: &str) -> bool {
    matches!(s, "completed" | "failed" | "cancelled" | "rejected")
}
fn field(v: &Value, name: &str) -> Result<String> {
    v[name]
        .as_str()
        .map(str::to_owned)
        .ok_or_else(|| anyhow!("Missing {name}"))
}
fn port_field(v: &Value) -> Result<u16> {
    let n = v["port"].as_u64().ok_or_else(|| anyhow!("Missing port"))?;
    ensure!(n > 0 && n <= 65535, "Invalid port");
    Ok(n as u16)
}
fn epoch() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}
fn valid_hash(s: &str) -> bool {
    s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit())
}
fn check_response(v: Value) -> Result<Value> {
    ensure!(
        v["ok"] == true,
        "{}",
        v["error"].as_str().unwrap_or("Peer rejected request")
    );
    Ok(v)
}
fn write_json<W: Write>(w: &mut W, v: &Value) -> Result<()> {
    let bytes = serde_json::to_vec(v)?;
    ensure!(bytes.len() <= CHUNK, "Metadata exceeds limit");
    w.write_all(&(bytes.len() as u32).to_be_bytes())?;
    w.write_all(&bytes)?;
    w.flush()?;
    Ok(())
}
fn read_json<R: Read>(r: &mut R) -> Result<Value> {
    let mut header = [0; 4];
    r.read_exact(&mut header)?;
    let length = u32::from_be_bytes(header) as usize;
    ensure!(length > 0 && length <= CHUNK, "Invalid metadata length");
    let mut bytes = vec![0; length];
    r.read_exact(&mut bytes)?;
    Ok(serde_json::from_slice(&bytes)?)
}
fn hash_reader<R: Read>(reader: &mut R, size: u64, job: Option<&Job>) -> Result<String> {
    hash_reader_progress(reader, size, job, || Ok(()))
}

fn hash_reader_progress<R: Read>(
    reader: &mut R,
    size: u64,
    job: Option<&Job>,
    mut progress: impl FnMut() -> Result<()>,
) -> Result<String> {
    let mut h = blake3::Hasher::new();
    let mut remaining = size;
    let mut buf = vec![0; CHUNK];
    while remaining > 0 {
        if let Some(j) = job {
            j.check()?;
        }
        let take = remaining.min(CHUNK as u64) as usize;
        reader.read_exact(&mut buf[..take])?;
        h.update(&buf[..take]);
        remaining -= take as u64;
        progress()?;
    }
    Ok(h.finalize().to_hex().to_string())
}

// Large files can take longer to hash than the ordinary socket idle timeout.
// Keep the peer informed while still checking cancellation between 1 MiB reads.
fn hash_with_heartbeat<R: Read, W: Write>(
    reader: &mut R,
    size: u64,
    job: &Job,
    stream: &mut W,
) -> Result<String> {
    let mut last = Instant::now();
    hash_reader_progress(reader, size, Some(job), || {
        if last.elapsed() >= Duration::from_secs(1) {
            write_json(stream, &json!({"ok": true, "hashing": true}))?;
            last = Instant::now();
        }
        Ok(())
    })
}

fn read_control<R: Read>(reader: &mut R, job: &Job) -> Result<Value> {
    loop {
        job.check()?;
        let value = read_json(reader)?;
        if value["ok"] != true || value["hashing"] != true {
            return Ok(value);
        }
    }
}
fn safe_name(name: &str) -> Result<()> {
    ensure!(
        !name.is_empty()
            && name.len() <= 240
            && name != "."
            && name != ".."
            && !name.ends_with(['.', ' '])
            && !name.starts_with('.')
            && !name
                .chars()
                .any(|c| c.is_control() || "/\\:*?\"<>|".contains(c)),
        "Unsafe file name"
    );
    let stem = name.split('.').next().unwrap().trim_end().to_uppercase();
    let device_digit = stem
        .strip_prefix("COM")
        .or_else(|| stem.strip_prefix("LPT"))
        .map(|s| {
            matches!(
                s,
                "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
            )
        })
        .unwrap_or(false);
    ensure!(
        !matches!(
            stem.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CLOCK$" | "CONIN$" | "CONOUT$"
        ) && !device_digit,
        "Reserved file name"
    );
    Ok(())
}
fn publish_no_replace(part: &Path, dir: &Path, name: &str) -> Result<PathBuf> {
    safe_name(name)?;
    for index in 0..10000 {
        let candidate = if index == 0 {
            dir.join(name)
        } else {
            let p = Path::new(name);
            let stem = p.file_stem().unwrap().to_string_lossy();
            let ext = p
                .extension()
                .map(|e| format!(".{}", e.to_string_lossy()))
                .unwrap_or_default();
            dir.join(format!("{stem} ({index}){ext}"))
        };
        match commit_no_replace(part, &candidate) {
            Ok(source_moved) => {
                OpenOptions::new()
                    .write(true)
                    .open(&candidate)?
                    .sync_all()?;
                #[cfg(unix)]
                File::open(dir)?.sync_all()?;
                if !source_moved { fs::remove_file(part)?; }
                return Ok(candidate);
            }
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => {
                return Err(e)
                    .context("Cannot commit received file without overwriting an existing file")
            }
        }
    }
    bail!("Too many files with this name")
}

#[cfg(all(not(target_env = "ohos"), not(windows)))]
fn commit_no_replace(part: &Path, candidate: &Path) -> std::io::Result<bool> {
    fs::hard_link(part, candidate).map(|()| false)
}

#[cfg(windows)]
fn commit_no_replace(part: &Path, candidate: &Path) -> std::io::Result<bool> {
    use std::os::windows::ffi::OsStrExt;
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn MoveFileExW(source: *const u16, destination: *const u16, flags: u32) -> i32;
    }
    let source: Vec<u16> = part.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = candidate.as_os_str().encode_wide().chain(Some(0)).collect();
    // No REPLACE_EXISTING: collisions are retried under a new filename.
    // COPY_ALLOWED supports private staging on C: and redirected Downloads
    // on another volume. WRITE_THROUGH flushes a cross-volume copy to disk.
    let result = unsafe { MoveFileExW(source.as_ptr(), destination.as_ptr(), 0x2 | 0x8) };
    if result == 0 { return Err(std::io::Error::last_os_error()); }
    // A successful cross-volume move can leave its source when deletion fails;
    // the ordinary owned-partial cleanup below handles that case as well.
    Ok(!part.exists())
}

#[cfg(target_env = "ohos")]
fn commit_no_replace(part: &Path, candidate: &Path) -> std::io::Result<bool> {
    use std::os::unix::ffi::OsStrExt;
    // Harmony's application sandbox denied hard links on the real test phone.
    // Prefer an atomic, no-overwrite rename (no second copy of large files).
    let source = CString::new(part.as_os_str().as_bytes())?;
    let target = CString::new(candidate.as_os_str().as_bytes())?;
    let result = unsafe { libc::renameat2(libc::AT_FDCWD, source.as_ptr(), libc::AT_FDCWD, target.as_ptr(), libc::RENAME_NOREPLACE) };
    if result == 0 { return Ok(true); }
    let error = std::io::Error::last_os_error();
    if matches!(error.raw_os_error(), Some(libc::ENOSYS | libc::EINVAL | libc::EOPNOTSUPP | libc::EPERM | libc::EACCES | libc::EXDEV)) {
        // Older/runtime-restricted implementations can still publish via an
        // exclusive new file. Never fall back to an overwriting rename/copy.
        copy_no_replace(part, candidate)?;
        Ok(false)
    } else { Err(error) }
}

#[cfg(any(test, target_env = "ohos"))]
fn copy_no_replace(part: &Path, candidate: &Path) -> std::io::Result<()> {
    let mut source = File::open(part)?;
    let mut target = OpenOptions::new().write(true).create_new(true).open(candidate)?;
    let result = (|| { std::io::copy(&mut source, &mut target)?; target.sync_all() })();
    drop(target);
    if result.is_err() { let _ = fs::remove_file(candidate); }
    result
}

fn open_partial(path: &Path) -> Result<File> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => ensure!(metadata.file_type().is_file(), "Unsafe partial file"),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
        Err(error) => return Err(error.into()),
    }
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true).truncate(false);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600).custom_flags(libc::O_NOFOLLOW);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // Open a reparse point itself rather than following its target.
        options.custom_flags(0x0020_0000);
    }
    let file = options.open(path)?;
    ensure!(
        file.metadata()?.file_type().is_file(),
        "Unsafe partial file"
    );
    Ok(file)
}
struct PartialGuard<'a> {
    core: &'a Core,
    key: String,
}
impl<'a> PartialGuard<'a> {
    fn acquire(core: &'a Core, key: &str) -> Result<Self> {
        ensure!(
            core.partials.lock().unwrap().insert(key.into()),
            "This file is already being received"
        );
        Ok(Self {
            core,
            key: key.into(),
        })
    }
}
impl Drop for PartialGuard<'_> {
    fn drop(&mut self) {
        self.core.partials.lock().unwrap().remove(&self.key);
    }
}
fn identity(dir: &Path) -> Result<(Vec<u8>, Vec<u8>)> {
    let path = dir.join("identity.json");
    if path.exists() {
        let v: Value = serde_json::from_slice(&fs::read(path)?)?;
        return Ok((
            hex::decode(field(&v, "cert")?)?,
            hex::decode(field(&v, "key")?)?,
        ));
    }
    let generated = rcgen::generate_simple_self_signed(vec!["sharelink.local".into()])?;
    let cert = generated.cert.der().to_vec();
    let key = generated.signing_key.serialize_der();
    let mut options = OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    let mut file = options.open(&path)?;
    file.write_all(&serde_json::to_vec(
        &json!({"cert":hex::encode(&cert),"key":hex::encode(&key)}),
    )?)?;
    file.sync_all()?;
    Ok((cert, key))
}
fn local_addresses() -> Vec<String> {
    let mut result = if_addrs::get_if_addrs()
        .unwrap_or_default()
        .into_iter()
        .filter_map(|i| match i.ip() {
            IpAddr::V4(ip) if !ip.is_loopback() && !ip.is_link_local() => Some(ip.to_string()),
            _ => None,
        })
        .collect::<Vec<_>>();
    result.sort();
    result.dedup();
    if result.is_empty() {
        result.push("127.0.0.1".into());
    }
    result
}
fn discovery_socket(port: u16) -> Result<UdpSocket> {
    use socket2::{Domain, Protocol, Socket, Type};
    let s = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP))?;
    s.set_reuse_address(true)?;
    s.set_broadcast(true)?;
    s.bind(&SocketAddr::from((Ipv4Addr::UNSPECIFIED, port)).into())?;
    s.set_read_timeout(Some(Duration::from_millis(500)))?;
    Ok(s.into())
}

#[derive(Debug)]
struct PinVerifier {
    expected: Option<String>,
    algorithms: rustls::crypto::WebPkiSupportedAlgorithms,
}
impl ServerCertVerifier for PinVerifier {
    fn verify_server_cert(
        &self,
        cert: &CertificateDer<'_>,
        _: &[CertificateDer<'_>],
        _: &ServerName<'_>,
        _: &[u8],
        _: UnixTime,
    ) -> std::result::Result<ServerCertVerified, rustls::Error> {
        if let Some(expected) = &self.expected {
            if hex::encode(Sha256::digest(cert.as_ref())) != *expected {
                return Err(rustls::Error::General(
                    "Receiver certificate fingerprint mismatch".into(),
                ));
            }
        }
        Ok(ServerCertVerified::assertion())
    }
    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls12_signature(message, cert, dss, &self.algorithms)
    }
    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        rustls::crypto::verify_tls13_signature(message, cert, dss, &self.algorithms)
    }
    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.algorithms.supported_schemes()
    }
}
#[cfg(test)]
fn connect(
    host: &str,
    port: u16,
    pin: Option<String>,
) -> Result<StreamOwned<ClientConnection, TcpStream>> {
    connect_from(host, port, pin, None)
}
fn connect_from(host: &str, port: u16, pin: Option<String>, source: Option<Ipv4Addr>) -> Result<StreamOwned<ClientConnection, TcpStream>> {
    let address = SocketAddr::new(
        host.parse::<IpAddr>()
            .context("Enter a numeric IPv4 address")?,
        port,
    );
    let socket = if let Some(source) = source {
        ensure!(address.is_ipv4(), "Direct transport requires IPv4");
        let socket = socket2::Socket::new(socket2::Domain::IPV4, socket2::Type::STREAM, Some(socket2::Protocol::TCP))?;
        socket.bind(&SocketAddr::from((source, 0)).into())?;
        socket.connect_timeout(&address.into(), Duration::from_secs(5))?;
        let socket: TcpStream = socket.into();
        socket
    } else { TcpStream::connect_timeout(&address, Duration::from_secs(5))? };
    socket.set_nodelay(true)?;
    socket.set_read_timeout(Some(Duration::from_secs(5)))?;
    socket.set_write_timeout(Some(Duration::from_secs(10)))?;
    let provider = rustls::crypto::ring::default_provider();
    let verifier = PinVerifier {
        expected: pin,
        algorithms: provider.signature_verification_algorithms,
    };
    let config = ClientConfig::builder_with_provider(Arc::new(provider))
        .with_protocol_versions(&[&rustls::version::TLS13])?
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(verifier))
        .with_no_client_auth();
    let mut conn = ClientConnection::new(
        Arc::new(config),
        ServerName::try_from("sharelink.local")?.to_owned(),
    )?;
    let mut socket = socket;
    while conn.is_handshaking() {
        conn.complete_io(&mut socket)?;
    }
    Ok(StreamOwned::new(conn, socket))
}

static HANDLES: OnceLock<Mutex<HashMap<u64, Arc<Core>>>> = OnceLock::new();
static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
fn handles() -> &'static Mutex<HashMap<u64, Arc<Core>>> {
    HANDLES.get_or_init(|| Mutex::new(HashMap::new()))
}
fn start_json(config: &str) -> Result<u64> {
    let core = Core::start(serde_json::from_str(config)?)?;
    let id = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed);
    handles().lock().unwrap().insert(id, core);
    Ok(id)
}
fn call_json(handle: u64, request: &str) -> String {
    let result = (|| -> Result<Value> {
        ensure!(request.len() <= CHUNK, "Request too large");
        let core = handles()
            .lock()
            .unwrap()
            .get(&handle)
            .cloned()
            .ok_or_else(|| anyhow!("Invalid core handle"))?;
        Ok(core.call(serde_json::from_str(request)?))
    })();
    match result {
        Ok(v) => v.to_string(),
        Err(e) => json!({"ok":false,"error":format!("{e:#}")}).to_string(),
    }
}
fn stop_handle(handle: u64) {
    if let Some(core) = handles().lock().unwrap().remove(&handle) {
        core.stop();
    }
}
#[no_mangle]
pub unsafe extern "C" fn sl_start(config: *const c_char) -> u64 {
    std::panic::catch_unwind(|| {
        if config.is_null() {
            return 0;
        }
        CStr::from_ptr(config)
            .to_str()
            .ok()
            .and_then(|s| start_json(s).ok())
            .unwrap_or(0)
    })
    .unwrap_or(0)
}
#[no_mangle]
pub unsafe extern "C" fn sl_call(handle: u64, request: *const c_char) -> *mut c_char {
    let response = std::panic::catch_unwind(|| {
        if request.is_null() {
            return json!({"ok":false,"error":"Null request"}).to_string();
        }
        match CStr::from_ptr(request).to_str() {
            Ok(s) => call_json(handle, s),
            Err(_) => json!({"ok":false,"error":"Request must be UTF-8"}).to_string(),
        }
    })
    .unwrap_or_else(|_| json!({"ok":false,"error":"Internal core error"}).to_string());
    CString::new(response).unwrap().into_raw()
}
#[no_mangle]
pub unsafe extern "C" fn sl_free(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}
#[no_mangle]
pub extern "C" fn sl_stop(handle: u64) {
    let _ = std::panic::catch_unwind(|| stop_handle(handle));
}

#[cfg(target_os = "android")]
mod android {
    use super::*;
    use jni::{
        objects::{JClass, JString},
        sys::{jlong, jstring},
        JNIEnv,
    };
    #[no_mangle]
    pub extern "system" fn Java_com_sharelink_app_NativeCore_start(
        mut env: JNIEnv,
        _class: JClass,
        config: JString,
    ) -> jlong {
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let s: String = match env.get_string(&config) {
                Ok(s) => s.into(),
                Err(_) => return 0,
            };
            start_json(&s).unwrap_or(0) as jlong
        }))
        .unwrap_or(0)
    }
    #[no_mangle]
    pub extern "system" fn Java_com_sharelink_app_NativeCore_call(
        mut env: JNIEnv,
        _class: JClass,
        handle: jlong,
        request: JString,
    ) -> jstring {
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let response = match env.get_string(&request) {
                Ok(s) => call_json(handle as u64, &String::from(s)),
                Err(_) => json!({"ok":false,"error":"Invalid request"}).to_string(),
            };
            env.new_string(response)
                .map(|s| s.into_raw())
                .unwrap_or(std::ptr::null_mut())
        }))
        .unwrap_or(std::ptr::null_mut())
    }
    #[no_mangle]
    pub extern "system" fn Java_com_sharelink_app_NativeCore_stop(
        _env: JNIEnv,
        _class: JClass,
        handle: jlong,
    ) {
        sl_stop(handle as u64);
    }
}

#[cfg(test)]
mod tests;

struct IncompleteCleanup { path: PathBuf, enabled: bool }
impl Drop for IncompleteCleanup {
    fn drop(&mut self) { if self.enabled { let _ = fs::remove_file(&self.path); } }
}
fn clean_owned_partials(directory: &Path) -> Result<()> {
    if !directory.exists() { return Ok(()); }
    ensure!(!fs::symlink_metadata(&directory)?.file_type().is_symlink(), "Unsafe partial directory");
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if name.len() > 5 && name.ends_with(".part") && name[..name.len()-5].bytes().all(|c| c.is_ascii_hexdigit() || c == b'-') && entry.file_type()?.is_file() {
            fs::remove_file(entry.path())?;
        }
    }
    Ok(())
}
