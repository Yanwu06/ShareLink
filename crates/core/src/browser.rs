//! Temporary, explicitly selected HTTP downloads for receivers without ShareLink.
use super::*;
use std::sync::atomic::AtomicU32;
use std::sync::RwLock;

struct Offered {
    id: String,
    name: String,
    source_path: PathBuf,
    source_fd: Option<i32>,
    file: File,
    size: u64,
    modified: Option<SystemTime>,
}
fn prepare_offers(sources: Vec<Source>) -> Result<Vec<Arc<Offered>>> {
    ensure!(!sources.is_empty() && sources.len() <= 64, "Choose 1 to 64 files");
    let mut files = Vec::new();
    for source in sources {
        safe_name(&source.name)?;
        let file = source.open()?; let metadata = file.metadata()?;
        ensure!(metadata.is_file(), "Source must be an ordinary file");
        files.push(Arc::new(Offered { id: uuid::Uuid::new_v4().simple().to_string(), name:source.name, source_path:source.path, source_fd:source.fd,
            file, size:metadata.len(), modified:metadata.modified().ok() }));
    }
    Ok(files)
}
#[derive(Default)]
struct Coverage {
    attempt: u64,
    ranges: Vec<(u64,u64)>,
    active: usize,
    successful: bool,
    interrupted: bool,
    wire_bytes: u64,
    samples: std::collections::VecDeque<(Instant,u64)>,
    last_write: Option<Instant>,
}
impl Coverage {
    fn begin(&mut self, start: u64) -> u64 {
        if start == 0 {
            let active = self.active;
            let attempt = self.attempt + 1;
            *self = Self { active, attempt, ..Self::default() };
        }
        self.active += 1;
        self.interrupted = false;
        self.attempt
    }
    fn add_for(&mut self, attempt: u64, begin: u64, end: u64) {
        if attempt == self.attempt { self.add(begin, end); }
    }
    fn finish(&mut self, attempt: u64, success: bool) {
        self.active -= 1;
        if attempt == self.attempt { self.successful |= success; self.interrupted |= !success; }
    }
    fn add(&mut self, mut begin: u64, mut end: u64) {
        let now = Instant::now();
        if self.samples.is_empty() { self.samples.push_back((now,self.wire_bytes)); }
        self.wire_bytes += end.saturating_sub(begin); self.last_write = Some(now);
        if now.duration_since(self.samples.back().unwrap().0).as_millis() >= 200 { self.samples.push_back((now,self.wire_bytes)); }
        while self.samples.len()>1 && now.duration_since(self.samples[1].0).as_millis()>=1500 { self.samples.pop_front(); }
        let mut result = Vec::new(); let mut inserted = false;
        for &(a,b) in &self.ranges {
            if b < begin { result.push((a,b)); }
            else if end < a { if !inserted { result.push((begin,end)); inserted = true; } result.push((a,b)); }
            else { begin = begin.min(a); end = end.max(b); }
        }
        if !inserted { result.push((begin,end)); } self.ranges = result;
    }
    fn bytes(&self) -> u64 { self.ranges.iter().map(|(a,b)| b-a).sum() }
    fn speed(&self) -> f64 {
        let now = Instant::now();
        if self.active==0 || self.last_write.is_none_or(|at| now.duration_since(at).as_secs_f64()>2.) { return 0.; }
        self.samples.front().map(|(at,bytes)| (self.wire_bytes-bytes) as f64 / now.duration_since(*at).as_secs_f64().max(0.05)).unwrap_or(0.)
    }
}
#[derive(Default)]
struct Receiver { visited: bool, files: HashMap<String,Coverage>, uploads: HashMap<String,UploadProgress>, last_seen: Option<Instant> }
struct UploadProgress { name: String, size: u64, coverage: Coverage }
#[derive(Default)]
struct Uploads { directory: Option<PathBuf>, files: u32 }
// An attempt stays visible after interruption; only a published file is complete.
struct UploadProgressGuard<'a> { state: &'a State, address: String, id: String, attempt: u64, success: bool }
impl<'a> UploadProgressGuard<'a> {
    fn begin(state: &'a State, address: String, name: String, size: u64) -> Self {
        let id = format!("upload-{}",uuid::Uuid::new_v4().simple());
        let mut coverage = Coverage::default(); let attempt = coverage.begin(0);
        let mut receivers = state.receivers.lock().unwrap();
        let receiver = receivers.entry(address.clone()).or_default(); receiver.last_seen = Some(Instant::now());
        receiver.uploads.insert(id.clone(), UploadProgress { name, size, coverage });
        Self { state, address, id, attempt, success: false }
    }
    fn written(&self, from: u64, to: u64) {
        if let Some(file) = self.state.receivers.lock().unwrap().get_mut(&self.address).and_then(|r| r.uploads.get_mut(&self.id)) { file.coverage.add_for(self.attempt,from,to); }
    }
    fn saved(&mut self, name: String) {
        if let Some(file) = self.state.receivers.lock().unwrap().get_mut(&self.address).and_then(|r| r.uploads.get_mut(&self.id)) { file.name = name; }
        self.success = true;
    }
}
impl Drop for UploadProgressGuard<'_> {
    fn drop(&mut self) {
        if let Some(file) = self.state.receivers.lock().unwrap().get_mut(&self.address).and_then(|r| r.uploads.get_mut(&self.id)) { file.coverage.finish(self.attempt,self.success); }
    }
}
struct PendingUpload(PathBuf);
impl Drop for PendingUpload { fn drop(&mut self) { let _ = fs::remove_file(&self.0); } }
struct State {
    address: SocketAddr,
    uploads: Mutex<Uploads>,
    upload_active: AtomicUsize,
    upload_token: String,
    session_id: String,
    files: RwLock<Vec<Arc<Offered>>>,
    listing_revision: AtomicU32,
    revoked: Mutex<std::collections::HashSet<String>>,
    stopped: AtomicBool,
    requests: AtomicUsize,
    sequence: AtomicU32,
    connections: Mutex<HashMap<u32,TcpStream>>,
    receivers: Mutex<HashMap<String,Receiver>>,
    max_receivers: Option<usize>,
    admitted: Mutex<std::collections::HashSet<IpAddr>>,
}
pub(super) struct Server { state: Arc<State>, accept: Option<thread::JoinHandle<()>> }
impl Server {
    #[cfg(test)]
    pub fn start(address: Ipv4Addr, port: u16, sources: Vec<Source>) -> Result<Self> {
        Self::start_limited(address, port, sources, None)
    }
    pub fn start_limited(address: Ipv4Addr, port: u16, sources: Vec<Source>, max_receivers: Option<usize>) -> Result<Self> {
        ensure!(max_receivers.is_none_or(|limit| limit > 0), "Receiver limit must be positive");
        ensure!(!address.is_unspecified() && (address.is_private() || address.is_loopback()), "A private local interface is required");
        let files = prepare_offers(sources)?;
        let listener = TcpListener::bind((address,port))?; listener.set_nonblocking(true)?;
        let state = Arc::new(State { address:listener.local_addr()?, uploads:Mutex::new(Uploads::default()), upload_active:AtomicUsize::new(0), upload_token:uuid::Uuid::new_v4().simple().to_string(), session_id:uuid::Uuid::new_v4().simple().to_string(), files:RwLock::new(files), listing_revision:AtomicU32::new(0), revoked:Mutex::new(std::collections::HashSet::new()), stopped:AtomicBool::new(false), requests:AtomicUsize::new(0),
            sequence:AtomicU32::new(0), connections:Mutex::new(HashMap::new()), receivers:Mutex::new(HashMap::new()), max_receivers, admitted:Mutex::new(std::collections::HashSet::new()) });
        let worker = state.clone();
        let accept = thread::Builder::new().name("browser-share".into()).spawn(move || {
            while !worker.stopped.load(Ordering::Acquire) {
                match listener.accept() {
                    Ok((mut socket,peer)) => {
                        // Windows accepted sockets inherit the nonblocking listener mode.
                        if socket.set_nonblocking(false).is_err() { continue; }
                        let _ = socket.set_write_timeout(Some(Duration::from_secs(4)));
                        let _ = socket.set_read_timeout(Some(Duration::from_secs(4)));
                        if worker.requests.fetch_add(1,Ordering::AcqRel) >= 16 {
                            worker.requests.fetch_sub(1,Ordering::AcqRel);
                            let _ = response(&mut socket,503,"Busy",b"Please retry",false,&[]); continue;
                        }
                        let id = worker.sequence.fetch_add(1,Ordering::Relaxed);
                        if let Ok(copy) = socket.try_clone() { worker.connections.lock().unwrap().insert(id,copy); }
                        let client = worker.clone();
                        let spawned = thread::Builder::new().name("browser-download".into()).spawn(move || {
                            let _ = serve(&mut socket,peer,&client);
                            client.connections.lock().unwrap().remove(&id);
                            client.requests.fetch_sub(1,Ordering::AcqRel);
                        });
                        if spawned.is_err() { worker.connections.lock().unwrap().remove(&id); worker.requests.fetch_sub(1,Ordering::AcqRel); }
                    }
                    Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => thread::sleep(Duration::from_millis(20)),
                    Err(_) => break,
                }
            }
        })?;
        Ok(Self {state,accept:Some(accept)})
    }
    pub fn snapshot(&self) -> Value {
        let upload_allowed = self.state.uploads.lock().unwrap().directory.is_some();
        let revoked = self.state.revoked.lock().unwrap();
        let receivers = self.state.receivers.lock().unwrap();
        let files = self.state.files.read().unwrap();
        json!({"url":format!("http://{}/",self.state.address), "port":self.state.address.port(), "uploadAllowed":upload_allowed, "sessionId":self.state.session_id,
            "activeDownloads":self.state.upload_active.load(Ordering::Acquire) + receivers.values().flat_map(|r|r.files.values()).map(|f|f.active).sum::<usize>(),
            "receivers":receivers.iter().map(|(address,receiver)| json!({"address":address,"mac":peer_mac(address,self.state.address.ip()),"pageVisited":receiver.visited,
                "connected":receiver.last_seen.is_some_and(|at| at.elapsed() <= Duration::from_secs(6)) || receiver.files.values().any(|f| f.active > 0) || receiver.uploads.values().any(|f| f.coverage.active > 0),
                "files":files.iter().filter(|f| !revoked.contains(&f.id)).filter_map(|f| receiver.files.get(&f.id).map(|p| json!({"direction":"download","id":f.id,"name":f.name,"size":f.size,
                    "attempt":p.attempt,"bytes":p.bytes(),"speed":p.speed(),"active":p.active,"complete":p.successful && p.bytes()==f.size,"interrupted":p.interrupted})))
                    .chain(receiver.uploads.iter().map(|(id,file)| { let p = &file.coverage; json!({"direction":"upload","id":id,"name":file.name,"size":file.size,
                        "attempt":p.attempt,"bytes":p.bytes(),"speed":p.speed(),"active":p.active,"complete":p.active==0 && p.successful && p.bytes()==file.size,"interrupted":p.interrupted}) })).collect::<Vec<_>>() })).collect::<Vec<_>>()})
    }
    pub fn allow_upload(&self, directory: &Path) -> Result<()> {
        self.set_upload_allowed(directory, true, None)
    }
    pub fn set_upload_allowed(&self, directory: &Path, allowed: bool, session: Option<&str>) -> Result<()> {
        if allowed { ensure!(directory.is_dir(), "Receive folder is unavailable"); }
        let mut uploads = self.state.uploads.lock().unwrap();
        ensure!(!self.state.stopped.load(Ordering::Acquire), "Sharing ended");
        ensure!(session.is_none_or(|id| id == self.state.session_id), "Browser session changed");
        // Admitted uploads own a directory clone; this changes only new admissions.
        uploads.directory = if allowed { Some(directory.to_path_buf()) } else { None };
        Ok(())
    }
    pub fn append(&self, sources: Vec<Source>) -> Result<()> {
        // Open and validate the complete batch before publishing any new URL.
        let prepared = prepare_offers(sources)?;
        let mut files = self.state.files.write().unwrap();
        ensure!(!self.state.stopped.load(Ordering::Acquire), "Sharing ended");
        ensure!(files.len() + prepared.len() <= 64, "每次分享最多添加64个文件，请结束后重新分享");
        files.extend(prepared);
        self.state.listing_revision.fetch_add(1, Ordering::Release);
        Ok(())
    }
    pub fn remove(&self, source: &Source) -> Result<()> {
        let offered = self.state.files.read().unwrap().iter().find(|f| f.source_path == source.path && f.source_fd == source.fd && f.name == source.name)
            .cloned().ok_or_else(|| anyhow!("Selected file is not currently shared"))?;
        // Share the admission lock with HTTP headers/begin, so a request cannot
        // slip between the active-download check and revocation.
        let mut revoked = self.state.revoked.lock().unwrap();
        let receivers = self.state.receivers.lock().unwrap();
        ensure!(!receivers.values().any(|r| r.files.values().any(|f| f.active > 0)), "Downloads are active");
        if revoked.insert(offered.id.clone()) { self.state.listing_revision.fetch_add(1, Ordering::Release); }
        Ok(())
    }
    pub fn stop(&mut self) -> Result<()> {
        { let _commit = self.state.uploads.lock().unwrap(); self.state.stopped.store(true,Ordering::Release); }
        for socket in self.state.connections.lock().unwrap().values() { let _ = socket.shutdown(Shutdown::Both); }
        if let Some(accept) = self.accept.take() { let _ = accept.join(); }
        let deadline = Instant::now()+Duration::from_secs(5);
        while self.state.requests.load(Ordering::Acquire)>0 && Instant::now()<deadline { thread::sleep(Duration::from_millis(10)); }
        ensure!(self.state.requests.load(Ordering::Acquire)==0,"Browser downloads are still closing"); Ok(())
    }
}
impl Drop for Server { fn drop(&mut self) { let _ = self.stop(); } }

fn escaped(value: &str) -> String {
    value.replace('&',"&amp;").replace('<',"&lt;").replace('>',"&gt;").replace('"',"&quot;").replace('\'',"&#39;")
}
fn human_size(bytes: u64) -> String {
    let units = ["B","KB","MB","GB","TB"]; let mut value=bytes as f64; let mut unit=0;
    while value>=1024. && unit<units.len()-1 { value/=1024.; unit+=1; }
    if unit==0 { format!("{bytes} B") } else if value<10. { format!("{value:.2} {}",units[unit]) } else { format!("{value:.1} {}",units[unit]) }
}
#[cfg(not(unix))]
fn peer_mac(_peer: &str, _local: IpAddr) -> Option<String> { None }
#[cfg(unix)]
fn peer_mac(peer: &str, local: IpAddr) -> Option<String> {
    // Ask the local interface's existing ARP entry; no scans or remote lookups.
    if let Ok(table)=std::fs::read_to_string("/proc/net/arp") {
        for line in table.lines().skip(1) {
            let fields=line.split_whitespace().collect::<Vec<_>>();
            if fields.len()>=6 && fields[0]==peer && fields[3].len()==17 && fields[3]!="00:00:00:00:00:00" { return Some(fields[3].to_uppercase()); }
        }
    }
    let peer: Ipv4Addr=peer.parse().ok()?;
    let adapter=if_addrs::get_if_addrs().ok()?.into_iter().find(|a| a.ip()==local)?;
    #[repr(C)] struct SockAddr { family:u16, data:[u8;14] }
    #[repr(C)] struct Arp { protocol:SockAddr, hardware:SockAddr, flags:i32, netmask:SockAddr, device:[u8;16] }
    let mut request=Arp { protocol:SockAddr {family:2,data:[0;14]},hardware:SockAddr {family:0,data:[0;14]},flags:0,
        netmask:SockAddr {family:0,data:[0;14]},device:[0;16] };
    request.protocol.data[2..6].copy_from_slice(&peer.octets());
    if adapter.name.len()>=16 { return None; }
    request.device[..adapter.name.len()].copy_from_slice(adapter.name.as_bytes());
    let fd=unsafe {libc::socket(libc::AF_INET,libc::SOCK_DGRAM,0)}; if fd<0 {return None;}
    let result=unsafe {libc::ioctl(fd,0x8954 as _,&mut request)}; unsafe {libc::close(fd)};
    if result!=0 || request.flags & 2 == 0 || request.hardware.data[..6].iter().all(|b|*b==0) {return None;}
    Some(request.hardware.data[..6].iter().map(|b| format!("{b:02X}")).collect::<Vec<_>>().join(":"))
}
const PAGE_STYLE: &str = include_str!("../../../shared/browser-page.css");
const PAGE_FOOTER: &str = include_str!("../../../shared/browser-footer.html");
const UPLOAD_SCRIPT: &str = include_str!("../../../shared/browser-upload.js");
const COPY_SCRIPT: &str = include_str!("../../../shared/browser-copy.js");
const LISTING_SCRIPT: &str = r#"<script>
(() => {
  const list = document.getElementById('shared-files');
  const sessionId = document.querySelector('main').dataset.sharelinkSession || '';
  let ended = false;
  function expire() {
    if(ended) return; ended=true; list.replaceChildren();
    let notice=document.getElementById('sharelink-session-status');
    if(!notice){notice=document.createElement('p');notice.id='sharelink-session-status';notice.setAttribute('role','status');list.before(notice);}
    notice.textContent='本次分享已结束，请重新打开分享页面。';
    window.dispatchEvent(new Event('sharelink-session-ended'));
  }
  window.addEventListener('sharelink-session-ended',expire);
  let revision = -1, polling = false;
  function size(bytes) { const units=['B','KB','MB','GB','TB']; let value=bytes,unit=0; while(value>=1024&&unit<4){value/=1024;unit++;} return unit===0?bytes+' B':value.toFixed(value<10?2:1)+' '+units[unit]; }
  async function sync() {
    if(polling || ended) return; polling=true;
    try {
      const response=await fetch('/manifest.json'+(sessionId?'?session='+encodeURIComponent(sessionId):''),{cache:'no-store'});
      if(response.status===410){expire();return;}
      if(!response.ok) return;
      const data=await response.json();
      if(sessionId && data.sessionId!==sessionId){expire();return;}
      if(data.revision===revision) return;
      const wanted=new Set(data.files.map(file=>file.id));
      for(const node of Array.from(list.children)) if(!wanted.has(node.dataset.fileId)) node.remove();
      const existing=new Map(Array.from(list.children).map(node=>[node.dataset.fileId,node]));
      for(const file of data.files) {
        if(existing.has(file.id)) continue;
        const item=document.createElement('li'); item.dataset.fileId=file.id;
        const name=document.createElement('div'); name.textContent=file.name;
        const detail=document.createElement('small'); detail.textContent=size(file.size);
        const link=document.createElement('a'); link.href='/files/'+file.id; link.download=''; link.textContent='下载';
        item.append(name,detail,link); list.append(item);
      }
      revision=data.revision;
    } catch(_) {} finally { polling=false; }
  }
  async function poll() { await sync(); setTimeout(poll,1000); }
  document.addEventListener('visibilitychange',()=>{if(!document.hidden)sync();});
  poll();
})();
</script>"#;
fn manifest(state: &State) -> Value {
    let revoked = state.revoked.lock().unwrap();
    let files = state.files.read().unwrap();
    json!({"sessionId":state.session_id,"uploadToken":if state.uploads.lock().unwrap().directory.is_some() {Some(&state.upload_token)} else {None}, "revision":state.listing_revision.load(Ordering::Acquire),
        "files":files.iter().filter(|file| !revoked.contains(&file.id)).map(|file|json!({"id":file.id,"name":file.name,"size":file.size})).collect::<Vec<_>>()})
}
fn page(state: &State) -> Vec<u8> {
    let revoked = state.revoked.lock().unwrap();
    let files = state.files.read().unwrap().iter().filter(|f| !revoked.contains(&f.id)).map(|f| format!("<li data-file-id=\"{}\"><div>{}</div><small>{}</small><a href=\"/files/{}\" download>下载</a></li>",f.id,escaped(&f.name),human_size(f.size),f.id)).collect::<String>();
    format!("<!doctype html><html lang=\"zh-CN\"><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\"><title>ShareLink 文件下载</title><style>{PAGE_STYLE}</style><main data-sharelink-session=\"{}\"><h1>ShareLink</h1><p>点击文件下载，并在浏览器中确认保存。</p><ul id=shared-files>{files}</ul><small>结束分享后，此页面将无法继续下载。</small>{PAGE_FOOTER}</main>{LISTING_SCRIPT}<script>{COPY_SCRIPT}{UPLOAD_SCRIPT}</script></html>",state.session_id).into_bytes()
}
fn response(socket: &mut TcpStream,status:u16,reason:&str,body:&[u8],head:bool,headers:&[(&str,String)]) -> Result<()> {
    let mut text = format!("HTTP/1.1 {status} {reason}\r\nContent-Length: {}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n",body.len());
    for (key,value) in headers { text.push_str(&format!("{key}: {value}\r\n")); }
    text.push_str("\r\n"); socket.write_all(text.as_bytes())?;
    if !head { socket.write_all(body)?; } Ok(())
}
fn range(value: Option<&String>,size:u64) -> Result<(u64,u64,bool)> {
    let Some(value) = value else { return Ok((0,size,false)); };
    let value = value.strip_prefix("bytes=").ok_or_else(||anyhow!("Invalid range"))?;
    ensure!(!value.contains(','),"Multiple ranges unsupported");
    let (from,to) = value.split_once('-').ok_or_else(||anyhow!("Invalid range"))?;
    if from.is_empty() {
        let suffix:u64 = to.parse()?; ensure!(suffix>0 && size>0,"Invalid suffix");
        return Ok((size.saturating_sub(suffix),size,true));
    }
    let begin:u64 = from.parse()?; let end = if to.is_empty() {size} else { to.parse::<u64>()?.saturating_add(1).min(size) };
    ensure!(begin<end && begin<size,"Range outside file"); Ok((begin,end,true))
}
fn upload(socket: &mut TcpStream, state: &State, peer: SocketAddr, headers: &HashMap<String,String>, initial: &[u8]) -> Result<Value> {
    let _active = CountGuard::acquire(&state.upload_active, 1)?;
    ensure!(headers.get("x-sharelink-upload") == Some(&state.upload_token), "Upload is not authorized");
    if let Some(origin) = headers.get("origin") { ensure!(origin == &format!("http://{}", state.address), "Invalid origin"); }
    ensure!(!headers.contains_key("transfer-encoding"), "Content-Length is required");
    let size: u64 = headers.get("content-length").ok_or_else(|| anyhow!("Missing length"))?.parse()?;

    let encoded = headers.get("x-sharelink-filename").ok_or_else(|| anyhow!("Missing file name"))?.as_bytes();
    let mut bytes = Vec::new(); let mut i = 0;
    while i < encoded.len() {
        if encoded[i] == b'%' { ensure!(i + 2 < encoded.len(), "Invalid file name"); bytes.push(u8::from_str_radix(std::str::from_utf8(&encoded[i+1..i+3])?,16)?); i += 3; }
        else { bytes.push(encoded[i]); i += 1; }
    }
    let name = String::from_utf8(bytes)?; safe_name(&name)?;
    ensure!(name.len() <= 180 && !name.starts_with(".sharelink-"), "Invalid file name");
    let directory = {
        let uploads = state.uploads.lock().unwrap();
        ensure!(uploads.files < 64, "Session upload file count reached");
        uploads.directory.clone().ok_or_else(|| anyhow!("Sender has not enabled uploads"))?
    };
    let mut progress = UploadProgressGuard::begin(state,peer.ip().to_string(),name.clone(),size);
    let pending = PendingUpload(directory.join(format!(".sharelink-upload-{}.part",uuid::Uuid::new_v4().simple())));
    let mut target = OpenOptions::new().write(true).create_new(true).open(&pending.0)?;
    ensure!(initial.len() as u64 <= size, "Body exceeds declared size"); target.write_all(initial)?;
    if !initial.is_empty() { progress.written(0,initial.len() as u64); }
    let mut received = initial.len() as u64; let mut block = [0u8;65536];
    while received < size {
        ensure!(!state.stopped.load(Ordering::Acquire), "Sharing ended");
        let remaining = (size-received).min(block.len() as u64) as usize;
        let n = socket.read(&mut block[..remaining])?; ensure!(n > 0,"Upload interrupted"); target.write_all(&block[..n])?; progress.written(received,received+n as u64); received += n as u64;
    }
    target.sync_all()?; drop(target);
    let mut uploads = state.uploads.lock().unwrap();
    ensure!(!state.stopped.load(Ordering::Acquire), "Sharing ended");
    let destination = publish_no_replace(&pending.0,&directory,&name)?;
    uploads.files += 1;
    progress.saved(destination.file_name().unwrap().to_string_lossy().into_owned());
    Ok(json!({"name":destination.file_name().unwrap().to_string_lossy(),"size":size}))
}
fn serve(socket: &mut TcpStream,peer:SocketAddr,state:&State) -> Result<()> {
    let mut raw = Vec::new(); let mut block = [0u8;1024];
    while !raw.windows(4).any(|b| b==b"\r\n\r\n") {
        let n = socket.read(&mut block)?; ensure!(n>0,"Peer left before headers"); raw.extend_from_slice(&block[..n]); ensure!(raw.len()<=16384,"Headers too large");
    }
    let header_end = raw.windows(4).position(|b| b == b"\r\n\r\n").unwrap() + 4;
    let text = std::str::from_utf8(&raw[..header_end])?; let mut lines = text.split("\r\n");
    let parts:Vec<_> = lines.next().unwrap_or("").split_whitespace().collect();
    ensure!(parts.len()==3,"Invalid HTTP request"); let head = parts[0]=="HEAD";
    if parts[0]!="GET" && parts[0]!="POST" && !head { return response(socket,405,"Method Not Allowed",b"GET or HEAD only",head,&[]); }
    let mut headers = HashMap::new();
    for line in lines.take_while(|line| !line.is_empty()) {
        let (name,value) = line.split_once(':').ok_or_else(||anyhow!("Invalid header"))?;
        let name = name.to_ascii_lowercase(); ensure!(!headers.contains_key(&name),"Duplicate header"); headers.insert(name,value.trim().to_owned());
    }
    let host = headers.get("host").map(|v|v.split(':').next().unwrap_or("")).unwrap_or("");
    if host != state.address.ip().to_string() { return response(socket,421,"Misdirected Request",b"Use the sender address",head,&[]); }
    let path = parts[1].split('?').next().unwrap_or("");
    let requested:Vec<_> = parts[1].split_once('?').map(|(_,q)|q.split('&').filter_map(|part|part.strip_prefix("session=")).collect()).unwrap_or_default();
    if ((path=="/manifest.json" || path=="/upload") && requested.is_empty() && !headers.contains_key("x-sharelink-session")) || (!requested.is_empty() && (requested.len()!=1 || requested[0]!=state.session_id)) || headers.get("x-sharelink-session").is_some_and(|id|id!=&state.session_id) {
        return response(socket,410,"Gone",b"Sharing session ended",head,&[]);
    }
    let receiver = peer.ip().to_string();
    let offered = path.strip_prefix("/files/").and_then(|id| state.files.read().unwrap().iter().find(|f| f.id==id).cloned());
    if path != "/" && path != "/manifest.json" && path != "/upload" && offered.is_none() { return response(socket,404,"Not Found",b"Not found",head,&[]); }
    // Serialize admission across page, HEAD, direct file and parallel range requests.
    // Retain the receiver until sharing ends, so progress always belongs to one device.
    let allowed = {
        let mut admitted = state.admitted.lock().unwrap();
        if admitted.contains(&peer.ip()) { true }
        else if state.max_receivers.is_none_or(|limit| admitted.len() < limit) { admitted.insert(peer.ip()); true }
        else { false }
    };
    if !allowed {
        return response(socket,403,"Forbidden","为保证传输体验，仅可使用一台设备连接。请发送方结束分享后重试。".as_bytes(),head,
            &[("Content-Type","text/plain; charset=utf-8".into())]);
    }
    if path == "/upload" {
        if parts[0] != "POST" { return response(socket,405,"Method Not Allowed",b"POST only",head,&[]); }
        return match upload(socket, state, peer, &headers, &raw[header_end..]) {
            Ok(body) => response(socket,200,"OK",body.to_string().as_bytes(),false,&[("Content-Type","application/json".into())]),
            Err(error) => response(socket,400,"Bad Request",error.to_string().as_bytes(),false,&[]),
        };
    }
    if parts[0] == "POST" { return response(socket,405,"Method Not Allowed",b"GET or HEAD only",false,&[]); }
    state.receivers.lock().unwrap().entry(receiver.clone()).or_default().last_seen = Some(Instant::now());
    if path=="/manifest.json" {
        return response(socket,200,"OK",manifest(state).to_string().as_bytes(),head,&[("Content-Type","application/json; charset=utf-8".into())]);
    }
    if path=="/" {
        state.receivers.lock().unwrap().entry(receiver).or_default().visited = true;
        return response(socket,200,"OK",&page(state),head,&[("Content-Type","text/html; charset=utf-8".into())]);
    }
    let file = offered.unwrap();
    let admission = state.revoked.lock().unwrap();
    if admission.contains(&file.id) { return response(socket,410,"Gone",b"File is no longer shared",head,&[]); }
    let metadata = file.file.metadata()?;
    if metadata.len()!=file.size || metadata.modified().ok()!=file.modified { return response(socket,410,"Gone",b"Source changed",head,&[]); }
    let validator = format!("\"{}\"",file.id);
    let requested = if headers.get("if-range").is_some_and(|v| v!=&validator) {None} else {headers.get("range")};
    let (begin,end,partial) = match range(requested,file.size) {
        Ok(value)=>value,
        Err(_)=>return response(socket,416,"Range Not Satisfiable",b"Range not satisfiable",head,&[("Content-Range",format!("bytes */{}",file.size))]),
    };
    let encoded = file.name.as_bytes().iter().map(|b| if b.is_ascii_alphanumeric() || b"-._~".contains(b) {(*b as char).to_string()} else {format!("%{b:02X}")}).collect::<String>();
    let mut header = format!("HTTP/1.1 {} {}\r\nContent-Type: application/octet-stream\r\nContent-Length: {}\r\nContent-Disposition: attachment; filename=\"download\"; filename*=UTF-8''{encoded}\r\nETag: {validator}\r\nAccept-Ranges: bytes\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n",if partial {206}else{200},if partial {"Partial Content"}else{"OK"},end-begin);
    if partial { header.push_str(&format!("Content-Range: bytes {}-{}/{}\r\n",begin,end-1,file.size)); } header.push_str("\r\n");
    socket.write_all(header.as_bytes())?; if head {return Ok(());}
    let attempt = {
        let mut receivers = state.receivers.lock().unwrap();
        let coverage = receivers.entry(receiver.clone()).or_default().files.entry(file.id.clone()).or_default();
        coverage.begin(begin)
    };
    drop(admission);
    let sent:Result<()> = (|| {
        let mut at = begin; let mut bytes = vec![0u8;65536];
        while at<end {
            ensure!(!state.stopped.load(Ordering::Acquire),"Sharing ended");
            let count = (end-at).min(bytes.len() as u64) as usize;
            #[cfg(unix)] let read = {use std::os::unix::fs::FileExt; file.file.read_at(&mut bytes[..count],at)?};
            #[cfg(windows)] let read = {use std::os::windows::fs::FileExt; file.file.seek_read(&mut bytes[..count],at)?};
            ensure!(read>0,"Source ended early"); socket.write_all(&bytes[..read])?;
            state.receivers.lock().unwrap().get_mut(&receiver).unwrap().files.get_mut(&file.id).unwrap().add_for(attempt,at,at+read as u64); at+=read as u64;
        } Ok(())
    })();
    {
        let mut receivers = state.receivers.lock().unwrap(); let progress = receivers.get_mut(&receiver).unwrap().files.get_mut(&file.id).unwrap();
        progress.finish(attempt, sent.is_ok());
    }
    sent
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn display_sizes_and_wire_speed_keep_range_progress_distinct() {
        assert_eq!(human_size(0),"0 B"); assert_eq!(human_size(65536),"64.0 KB");
        assert_eq!(human_size(9112440832),"8.49 GB");
        let mut coverage=Coverage {active:1,..Coverage::default()};
        coverage.add(0,1000); coverage.add(0,1000);
        assert_eq!(coverage.bytes(),1000); assert_eq!(coverage.wire_bytes,2000);
        assert!(coverage.speed()>0.); coverage.active=0; assert_eq!(coverage.speed(),0.);
    }
    #[test]
    fn fresh_download_resets_progress_while_ranges_resume_and_old_workers_cannot_taint_retry() {
        let mut p = Coverage::default();
        let first=p.begin(0);p.add_for(first,0,800);p.finish(first,false);
        let resumed=p.begin(800);assert_eq!(resumed,first);assert_eq!(p.bytes(),800);
        p.add_for(resumed,800,900);
        let retry=p.begin(0);assert!(retry>first);assert_eq!(p.bytes(),0);assert_eq!(p.active,2);
        p.add_for(first,900,1000);p.finish(first,false);
        assert_eq!(p.bytes(),0);assert!(!p.interrupted);assert_eq!(p.active,1);
        p.add_for(retry,0,100);assert_eq!(p.bytes(),100);p.finish(retry,true);
        assert_eq!(p.active,0);assert!(p.successful);
        let again=p.begin(0);assert_eq!(p.bytes(),0);assert!(!p.successful);p.finish(again,false);
    }
    #[test]
    fn old_page_session_manifest_and_upload_cannot_cross_restarted_share() {
        let temp=tempfile::tempdir().unwrap();let source=temp.path().join("source.txt");fs::write(&source,b"generated").unwrap();let received=temp.path().join("received");fs::create_dir(&received).unwrap();
        let sources=||vec![Source{path:source.clone(),name:"source.txt".into(),fd:None}];
        let mut first=Server::start(Ipv4Addr::LOCALHOST,0,sources()).unwrap();let address=first.state.address;let old=first.state.session_id.clone();let html=String::from_utf8(page(&first.state)).unwrap();assert!(html.contains(&format!("data-sharelink-session=\"{old}\"")));assert!(!html.contains(&first.state.upload_token));first.stop().unwrap();
        let mut second=Server::start(Ipv4Addr::LOCALHOST,address.port(),sources()).unwrap();assert_ne!(old,second.state.session_id);second.allow_upload(&received).unwrap();let token=&second.state.upload_token;let current=&second.state.session_id;
        let stale=get(address,format!("GET /manifest.json?session={old} HTTP/1.1\r\nHost: {address}\r\n\r\n"));assert!(stale.starts_with(b"HTTP/1.1 410"));assert!(!String::from_utf8_lossy(&stale).contains(token));
        let fresh=get(address,format!("GET /manifest.json?session={current} HTTP/1.1\r\nHost: {address}\r\n\r\n"));assert!(fresh.starts_with(b"HTTP/1.1 200"));
        let duplicate=get(address,format!("GET /manifest.json?session={current}&session={old} HTTP/1.1\r\nHost: {address}\r\n\r\n"));assert!(duplicate.starts_with(b"HTTP/1.1 410"));
        let stale=get(address,format!("POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {old}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: stale.txt\r\nContent-Length: 4\r\n\r\ntest"));assert!(stale.starts_with(b"HTTP/1.1 410"));assert!(!received.join("stale.txt").exists());
        assert_eq!(second.snapshot()["activeDownloads"],0);second.stop().unwrap();
    }
    #[test]
    fn live_upload_progress_tracks_written_bytes_commit_and_interruption() {
        let temp=tempfile::tempdir().unwrap(); let source=temp.path().join("source.txt"); fs::write(&source,b"source").unwrap();
        let received=temp.path().join("received"); fs::create_dir(&received).unwrap();
        let mut server=Server::start(Ipv4Addr::LOCALHOST,0,vec![Source{path:source,name:"source.txt".into(),fd:None}]).unwrap();
        server.allow_upload(&received).unwrap(); let address=server.state.address; let token=server.state.upload_token.clone(); let session=server.state.session_id.clone();
        let find=|name:&str| -> Value { server.snapshot()["receivers"].as_array().unwrap().iter().flat_map(|r|r["files"].as_array().unwrap()).find(|f|f["name"]==name).cloned().unwrap_or(Value::Null) };
        let mut socket=TcpStream::connect(address).unwrap();socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        write!(socket,"POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: paced.bin\r\nContent-Length: 65536\r\n\r\n").unwrap();
        let body=vec![7u8;65536];socket.write_all(&body[..16384]).unwrap();
        for _ in 0..100 {if find("paced.bin")["bytes"].as_u64().unwrap_or(0)>0 {break;} thread::sleep(Duration::from_millis(10));}
        let live=find("paced.bin");assert_eq!(live["direction"],"upload");assert_eq!(live["active"],1);assert_eq!(live["complete"],false);assert!(live["bytes"].as_u64().unwrap()>0&&live["bytes"].as_u64().unwrap()<65536);assert_eq!(server.snapshot()["activeDownloads"],1);
        socket.write_all(&body[16384..]).unwrap();let mut response=Vec::new();socket.read_to_end(&mut response).unwrap();assert!(response.starts_with(b"HTTP/1.1 200"));
        let done=find("paced.bin");assert_eq!(done["bytes"],65536);assert_eq!(done["active"],0);assert_eq!(done["complete"],true);assert_eq!(fs::read(received.join("paced.bin")).unwrap(),body);
        let mut socket=TcpStream::connect(address).unwrap();socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        write!(socket,"POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: interrupted.bin\r\nContent-Length: 65536\r\n\r\n").unwrap();socket.write_all(&body[..8192]).unwrap();socket.shutdown(Shutdown::Write).unwrap();let mut response=Vec::new();socket.read_to_end(&mut response).unwrap();
        let failed=find("interrupted.bin");assert_eq!(failed["bytes"],8192);assert_eq!(failed["active"],0);assert_eq!(failed["interrupted"],true);assert_eq!(failed["complete"],false);assert!(!received.join("interrupted.bin").exists());
        assert!(!fs::read_dir(&received).unwrap().any(|p|p.unwrap().file_name().to_string_lossy().ends_with(".part")));
        server.stop().unwrap();
    }
    #[test]
    fn upload_switch_rejects_new_requests_but_finishes_admitted_uploads() {
        let root=tempfile::tempdir().unwrap(); let source=root.path().join("source.txt"); fs::write(&source,b"selected").unwrap();
        let received=root.path().join("received"); fs::create_dir(&received).unwrap();
        let mut server=Server::start(Ipv4Addr::LOCALHOST,0,vec![Source{path:source,name:"source.txt".into(),fd:None}]).unwrap();
        let address=server.state.address; let session=server.state.session_id.clone(); let token=server.state.upload_token.clone();
        let file_id=server.state.files.read().unwrap()[0].id.clone();
        assert_eq!(server.snapshot()["uploadAllowed"],false);
        server.set_upload_allowed(&received,true,Some(&session)).unwrap();
        assert!(server.set_upload_allowed(&received,false,Some("stale-session")).is_err());
        assert_eq!(server.snapshot()["uploadAllowed"],true);
        let mut socket=TcpStream::connect(address).unwrap(); socket.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        write!(socket,"POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: admitted.bin\r\nContent-Length: 65536\r\n\r\n").unwrap();
        let body=vec![9u8;65536]; socket.write_all(&body[..16384]).unwrap();
        for _ in 0..100 { if server.snapshot()["activeDownloads"].as_u64().unwrap_or(0)>0 && server.state.receivers.lock().unwrap().values().any(|r|r.uploads.values().any(|f|f.coverage.bytes()>0)){break;} thread::sleep(Duration::from_millis(10)); }
        assert!(server.state.receivers.lock().unwrap().values().any(|r|r.uploads.values().any(|f|f.coverage.bytes()>0)));
        server.set_upload_allowed(&received,false,Some(&session)).unwrap();
        assert_eq!(server.snapshot()["uploadAllowed"],false); assert!(manifest(&server.state)["uploadToken"].is_null());
        let rejected=get(address,format!("POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: denied.bin\r\nContent-Length: 4\r\n\r\ntest"));
        assert!(rejected.starts_with(b"HTTP/1.1 400")); assert!(!received.join("denied.bin").exists());
        assert!(get(address,format!("GET /files/{file_id} HTTP/1.1\r\nHost: {address}\r\n\r\n")).ends_with(b"selected"));
        socket.write_all(&body[16384..]).unwrap(); let mut response=Vec::new(); socket.read_to_end(&mut response).unwrap();
        assert!(response.starts_with(b"HTTP/1.1 200")); assert_eq!(fs::read(received.join("admitted.bin")).unwrap(),body);
        server.set_upload_allowed(&received,true,Some(&session)).unwrap(); assert_eq!(server.state.uploads.lock().unwrap().files,1);
        server.stop().unwrap();
    }
    #[test]
    fn uploads_require_consent_preserve_names_reject_paths_and_remove_aborted_parts() {
        let root = tempfile::tempdir().unwrap(); let source = root.path().join("source.txt"); fs::write(&source,b"selected").unwrap();
        let received=root.path().join("received");fs::create_dir(&received).unwrap();
        let mut server=Server::start(Ipv4Addr::LOCALHOST,0,vec![Source{path:source,name:"source.txt".into(),fd:None}]).unwrap();
        let address=server.state.address;let token=server.state.upload_token.clone(); let session=server.state.session_id.clone();
        let post=|name:&str,auth:&str,body:&str|get(address,format!("POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Filename: {name}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {auth}\r\nContent-Length: {}\r\n\r\n{body}",body.len()));
        assert!(post("a.txt",&token,"test").starts_with(b"HTTP/1.1 400"));server.allow_upload(&received).unwrap();
        assert!(post("a.txt","wrong","test").starts_with(b"HTTP/1.1 400"));
        assert!(post("..%2Fescape.txt",&token,"test").starts_with(b"HTTP/1.1 400"));
        assert!(post("a.txt",&token,"test").starts_with(b"HTTP/1.1 200"));
        assert!(post("a.txt",&token,"second").starts_with(b"HTTP/1.1 200"));
        assert_eq!(fs::read(received.join("a.txt")).unwrap(),b"test");assert_eq!(fs::read(received.join("a (1).txt")).unwrap(),b"second");
        assert!(post("empty.txt",&token,"").starts_with(b"HTTP/1.1 200"));
        let mut interrupted=TcpStream::connect(address).unwrap();write!(interrupted,"POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Filename: abort.txt\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nContent-Length: 100\r\n\r\npartial").unwrap(); interrupted.shutdown(Shutdown::Write).unwrap();let mut discarded=Vec::new();let _=interrupted.read_to_end(&mut discarded);
        assert!(!received.join("abort.txt").exists());assert!(!fs::read_dir(&received).unwrap().any(|p|p.unwrap().file_name().to_string_lossy().ends_with(".part")));
        server.stop().unwrap();assert!(TcpStream::connect(address).is_err());
    }
    fn get(address:SocketAddr,request:String) -> Vec<u8> {
        let mut client = TcpStream::connect(address).unwrap(); client.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        client.write_all(request.as_bytes()).unwrap(); let mut data = Vec::new(); client.read_to_end(&mut data).unwrap(); data
    }
    #[test]
    fn live_http_has_direct_listing_ranges_independent_positions_and_shutdown() {
        let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("file.bin");
        let bytes:Vec<_> = (0..65536).map(|i|(i%251) as u8).collect(); fs::write(&path,&bytes).unwrap();
        let mut server = Server::start(Ipv4Addr::LOCALHOST,0,vec![Source {path:path.clone(),fd:None,name:"测试&file.bin".into()}]).unwrap();
        let address=server.state.address; let id=server.state.files.read().unwrap()[0].id.clone();
        let root=get(address,format!("GET / HTTP/1.1\r\nHost: {address}\r\n\r\n"));
        let html=String::from_utf8(root).unwrap(); assert!(html.contains("测试&amp;file.bin")); assert!(!html.contains("<form"));
        assert!(html.contains("Windows端：") && html.contains("HarmonyOS NEXT端：") && html.contains("请前往华为应用商店搜索ShareLink下载。"));
        assert!(html.contains("href=\"https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/download/latest/ShareLink-Setup-x64.exe\""));
        assert!(html.contains("data-copy-value=\"https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/download/latest/ShareLink-Setup-x64.exe\""));
        assert!(html.contains("https://ifdian.net/a/yanwu06/plan") && html.contains("execCommand('copy')"));
        assert!(!html.contains("让文件自由流转"));
        if let Ok(output) = std::env::var("SHARELINK_WEB_TEST_HTML") { fs::write(output, html.split_once("\r\n\r\n").unwrap().1).unwrap(); }
        let one=thread::spawn({let id=id.clone();move||get(address,format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=13-57\r\n\r\n"))});
        let two=get(address,format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=30-100\r\n\r\n"));
        assert!(one.join().unwrap().ends_with(&bytes[13..58])); assert!(two.ends_with(&bytes[30..101]));
        let snap=server.snapshot(); assert_eq!(snap["receivers"][0]["files"][0]["bytes"],88);
        let head=get(address,format!("HEAD /files/{id} HTTP/1.1\r\nHost: {address}\r\n\r\n")); assert!(head.ends_with(b"\r\n\r\n"));
        let bad=get(address,format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=65536-\r\n\r\n")); assert!(bad.starts_with(b"HTTP/1.1 416"));
        let host=get(address,"GET / HTTP/1.1\r\nHost: other.invalid\r\n\r\n".into()); assert!(host.starts_with(b"HTTP/1.1 421"));
        let full=get(address,format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\n\r\n")); assert!(full.ends_with(&bytes));
        assert_eq!(server.snapshot()["receivers"][0]["files"][0]["bytes"],65536);
        fs::write(&path,b"changed").unwrap(); let changed=get(address,format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\n\r\n")); assert!(changed.starts_with(b"HTTP/1.1 410"));
        server.stop().unwrap(); assert!(TcpStream::connect(address).is_err());
    }
    #[test]
    fn live_http_restart_and_range_resume_have_distinct_attempts() {
        let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("restart.bin");
        let bytes = vec![71u8; 16 * 1024 * 1024]; fs::write(&path, &bytes).unwrap();
        let mut server = Server::start(Ipv4Addr::LOCALHOST, 0, vec![Source {path:path.clone(), fd:None, name:"restart.bin".into()}]).unwrap();
        let address = server.state.address; let id = server.state.files.read().unwrap()[0].id.clone();
        let full = format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\n\r\n");
        let original = get(address, full.clone()); assert!(original.ends_with(&bytes), "len={} head={}", original.len(), String::from_utf8_lossy(&original[..original.len().min(180)]));
        let first = server.snapshot()["receivers"][0]["files"][0]["attempt"].as_u64().unwrap();
        let resumed = get(address, format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=8388608-8389631\r\n\r\n"));
        assert!(resumed.starts_with(b"HTTP/1.1 206"));
        assert_eq!(server.snapshot()["receivers"][0]["files"][0]["attempt"], first);
        let mut client = TcpStream::connect(address).unwrap();
        client.set_read_timeout(Some(Duration::from_secs(3))).unwrap(); client.write_all(full.as_bytes()).unwrap();
        let mut headers = Vec::new(); let mut byte = [0u8; 1];
        while !headers.ends_with(b"\r\n\r\n") { client.read_exact(&mut byte).unwrap(); headers.push(byte[0]); }
        for _ in 0..100 {
            if server.snapshot()["receivers"][0]["files"][0]["attempt"].as_u64().unwrap() > first { break; }
            thread::sleep(Duration::from_millis(5));
        }
        let snapshot = server.snapshot(); let restarted = &snapshot["receivers"][0]["files"][0];
        assert!(restarted["attempt"].as_u64().unwrap() > first);
        assert!(restarted["bytes"].as_u64().unwrap() < bytes.len() as u64, "Fresh GET must not retain previous completed progress");
        assert_eq!(restarted["complete"], false);
        assert!(server.remove(&Source {path, fd:None, name:"restart.bin".into()}).is_err(), "An admitted active download must block removal");
        drop(client); server.stop().unwrap();
    }
    #[test]
    fn removing_idle_offers_preserves_other_urls_ranges_and_listener() {
        let dir = tempfile::tempdir().unwrap(); let one = dir.path().join("one.bin"); let two = dir.path().join("two.bin");
        fs::write(&one, vec![1u8; 1024]).unwrap(); fs::write(&two, vec![2u8; 1024]).unwrap();
        let mut server = Server::start(Ipv4Addr::LOCALHOST, 0, vec![Source {path:one.clone(),fd:None,name:"same.bin".into()}, Source {path:two.clone(),fd:None,name:"same.bin".into()}]).unwrap();
        let address=server.state.address; let id1=server.state.files.read().unwrap()[0].id.clone(); let id2=server.state.files.read().unwrap()[1].id.clone();
        let partial=get(address,format!("GET /files/{id2} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=10-99\r\n\r\n")); assert!(partial.starts_with(b"HTTP/1.1 206"));
        server.remove(&Source {path:one,fd:None,name:"same.bin".into()}).unwrap();
        let page=String::from_utf8(get(address,format!("GET / HTTP/1.1\r\nHost: {address}\r\n\r\n"))).unwrap();assert!(!page.contains(&id1));assert!(page.contains(&id2));
        let revoked=get(address,format!("GET /files/{id1} HTTP/1.1\r\nHost: {address}\r\n\r\n"));assert!(revoked.starts_with(b"HTTP/1.1 410"));
        let resumed=get(address,format!("GET /files/{id2} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=100-199\r\n\r\n")); assert!(resumed.starts_with(b"HTTP/1.1 206"));
        let snapshot=server.snapshot();assert_eq!(snapshot["receivers"][0]["files"][0]["id"],id2);assert_eq!(snapshot["receivers"][0]["files"][0]["bytes"],190);
        assert_eq!(server.state.address,address);server.stop().unwrap();
    }
    fn get_as(state: Arc<State>, peer: Ipv4Addr, request: String) -> Vec<u8> {
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let address = listener.local_addr().unwrap();
        let worker = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            serve(&mut socket, SocketAddr::new(peer.into(), 12345), &state).unwrap();
        });
        let data = get(address, request); worker.join().unwrap(); data
    }
    #[test]
    fn single_receiver_rejects_other_pages_heads_and_files_but_keeps_parallel_ranges() {
        let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("single.bin");
        let bytes = vec![42u8; 1024]; fs::write(&path, &bytes).unwrap();
        let source = || vec![Source { path:path.clone(), fd:None, name:"single.bin".into() }];
        let mut server = Server::start_limited(Ipv4Addr::LOCALHOST, 0, source(), Some(1)).unwrap();
        let address = server.state.address; let id = server.state.files.read().unwrap()[0].id.clone();
        let session=server.state.session_id.clone();
        let request = |method: &str, route: &str| format!("{method} {route} HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\n\r\n");
        let first = Ipv4Addr::new(192,168,49,2); let second = Ipv4Addr::new(192,168,49,3);
        // A favicon or invalid host cannot consume the one receiver slot.
        assert!(get_as(server.state.clone(), second, request("GET", "/favicon.ico")).starts_with(b"HTTP/1.1 404"));
        assert!(get_as(server.state.clone(), second, "GET / HTTP/1.1\r\nHost: invalid\r\n\r\n".into()).starts_with(b"HTTP/1.1 421"));
        assert!(get_as(server.state.clone(), first, request("GET", "/")).starts_with(b"HTTP/1.1 200"));
        for (method, route) in [("GET", "/"), ("HEAD", "/"), ("GET", "/manifest.json"), ("HEAD", "/manifest.json"), ("HEAD", &*format!("/files/{id}")), ("GET", &*format!("/files/{id}"))] {
            assert!(get_as(server.state.clone(), second, request(method, route)).starts_with(b"HTTP/1.1 403"));
        }
        let range_request = |begin, end| format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\nRange: bytes={begin}-{end}\r\n\r\n");
        let state = server.state.clone(); let req = range_request(0,511);
        let one = thread::spawn(move || get_as(state, first, req));
        for _ in 0..100 { if !server.state.receivers.lock().unwrap().values().next().unwrap().files.is_empty() { break; } thread::sleep(Duration::from_millis(2)); }
        let two = get_as(server.state.clone(), first, range_request(512,1023));
        assert!(one.join().unwrap().ends_with(&bytes[..512])); assert!(two.ends_with(&bytes[512..]));
        assert_eq!(server.snapshot()["receivers"].as_array().unwrap().len(), 1);
        assert_eq!(server.snapshot()["receivers"][0]["files"][0]["bytes"], 1024);
        server.stop().unwrap();
        let mut restarted = Server::start_limited(Ipv4Addr::LOCALHOST, 0, source(), Some(1)).unwrap();
        let address = restarted.state.address;
        assert!(get_as(restarted.state.clone(), second, format!("GET / HTTP/1.1\r\nHost: {address}\r\n\r\n")).starts_with(b"HTTP/1.1 200"));
        restarted.stop().unwrap();
    }
    #[test]
    fn simultaneous_receivers_cannot_both_claim_single_receiver_slot() {
        let dir = tempfile::tempdir().unwrap(); let path = dir.path().join("race.bin"); fs::write(&path,b"race").unwrap();
        let mut server = Server::start_limited(Ipv4Addr::LOCALHOST,0,vec![Source {path,fd:None,name:"race.bin".into()}],Some(1)).unwrap();
        let barrier = Arc::new(std::sync::Barrier::new(2));
        let workers: Vec<_> = [2,3].into_iter().map(|last| {
            let state = server.state.clone(); let barrier = barrier.clone();
            thread::spawn(move || {
                barrier.wait(); let address = state.address;
                get_as(state, Ipv4Addr::new(192,168,49,last), format!("GET / HTTP/1.1\r\nHost: {address}\r\n\r\n"))
            })
        }).collect();
        let responses: Vec<_> = workers.into_iter().map(|worker| worker.join().unwrap()).collect();
        assert_eq!(responses.iter().filter(|r|r.starts_with(b"HTTP/1.1 200")).count(),1);
        assert_eq!(responses.iter().filter(|r|r.starts_with(b"HTTP/1.1 403")).count(),1);
        assert_eq!(server.snapshot()["receivers"].as_array().unwrap().len(),1);
        server.stop().unwrap();
    }
    #[cfg(windows)]
    #[test]
    fn large_capacity_actual_sparse_source_and_http_offsets_preserve_more_than_40_gib() {
        let dir=tempfile::tempdir().unwrap();let path=dir.path().join("generated-41-gib.bin");File::create(&path).unwrap();
        let marked=std::process::Command::new("fsutil.exe").args(["sparse","setflag"]).arg(&path).output().unwrap();assert!(marked.status.success(),"Sparse mark failed; do not extend an ordinary file");
        let size=41u64*1024*1024*1024+97;let offset=40u64*1024*1024*1024+13;let marker=b"ShareLink 64-bit offset marker";
        {let mut file=OpenOptions::new().write(true).open(&path).unwrap();file.set_len(size).unwrap();file.seek(SeekFrom::Start(offset)).unwrap();file.write_all(marker).unwrap();}
        let source=||Source {path:path.clone(),fd:None,name:"large.bin".into()};assert_eq!(source_entries(&[source()]).unwrap()[0].size,size);
        let mut server=Server::start(Ipv4Addr::LOCALHOST,0,vec![source()]).unwrap();let address=server.state.address;let id=server.state.files.read().unwrap()[0].id.clone();
        assert_eq!(manifest(&server.state)["files"][0]["size"],size);
        let head=get(address,format!("HEAD /files/{id} HTTP/1.1\r\nHost: {address}\r\n\r\n"));assert!(String::from_utf8(head).unwrap().contains(&format!("Content-Length: {size}\r\n")));
        let response=get(address,format!("GET /files/{id} HTTP/1.1\r\nHost: {address}\r\nRange: bytes={offset}-{}\r\n\r\n",offset+marker.len() as u64-1));assert!(response.starts_with(b"HTTP/1.1 206"));assert!(response.ends_with(marker));assert!(String::from_utf8_lossy(&response).contains(&format!("Content-Range: bytes {offset}-{}/{size}",offset+marker.len() as u64-1)));
        server.stop().unwrap();
    }
    #[test]
    fn large_capacity_upload_headers_have_no_fixed_size_or_session_byte_quota() {
        use std::net::Shutdown;
        let root=tempfile::tempdir().unwrap();let source=root.path().join("source.txt");fs::write(&source,b"selected").unwrap();let received=root.path().join("received");fs::create_dir(&received).unwrap();
        let mut server=Server::start(Ipv4Addr::LOCALHOST,0,vec![Source{path:source,name:"source.txt".into(),fd:None}]).unwrap();server.allow_upload(&received).unwrap();
        let address=server.state.address;let token=server.state.upload_token.clone();let session=server.state.session_id.clone();
        for (name,size) in [("41-gib.bin",41u64*1024*1024*1024),("above-old-8-tib.bin",8u64*1024*1024*1024*1024+1),("u64-max.bin",u64::MAX)] {
            let mut socket=TcpStream::connect(address).unwrap();socket.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
            write!(socket,"POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: {name}\r\nContent-Length: {size}\r\n\r\n").unwrap();socket.write_all(&[37u8;8192]).unwrap();
            let mut live=Value::Null;
            for _ in 0..200 {live=server.snapshot()["receivers"][0]["files"].as_array().and_then(|files|files.iter().find(|f|f["name"]==name)).cloned().unwrap_or(Value::Null);if live["bytes"]==8192 {break;}thread::sleep(Duration::from_millis(10));}
            assert_eq!(live["size"],size,"large declared size must reach streaming reception");assert_eq!(live["bytes"],8192);assert_eq!(live["active"],1);assert_eq!(live["complete"],false);
            socket.shutdown(Shutdown::Write).unwrap();let mut response=Vec::new();socket.read_to_end(&mut response).unwrap();assert!(String::from_utf8_lossy(&response).contains("Upload interrupted"));
            assert!(!received.join(name).exists());assert!(!fs::read_dir(&received).unwrap().any(|p|p.unwrap().file_name().to_string_lossy().ends_with(".part")));
        }
        // Interrupted huge declarations must not consume any of the 64 committed-file slots.
        for n in 0..64 {assert!(get(address,format!("POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: empty-{n}.txt\r\nContent-Length: 0\r\n\r\n")).starts_with(b"HTTP/1.1 200"));}
        let full=get(address,format!("POST /upload HTTP/1.1\r\nHost: {address}\r\nX-ShareLink-Session: {session}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: over-count.txt\r\nContent-Length: 0\r\n\r\n"));assert!(String::from_utf8_lossy(&full).contains("Session upload file count reached"));
        assert_eq!(fs::read_dir(&received).unwrap().count(),64);server.stop().unwrap();
    }
    #[test]
    fn range_validation_covers_suffix_empty_and_out_of_bounds() {
        assert_eq!(range(Some(&"bytes=-3".into()),10).unwrap(),(7,10,true));
        assert_eq!(range(Some(&"bytes=7-".into()),10).unwrap(),(7,10,true));
        assert!(range(Some(&"bytes=-0".into()),10).is_err()); assert!(range(Some(&"bytes=0-".into()),0).is_err());
        assert_eq!(range(None,0).unwrap(),(0,0,false));
    }

    #[test]
    fn append_during_download_keeps_existing_stream_urls_progress_and_receiver_slot() {
        let dir=tempfile::tempdir().unwrap(); let one=dir.path().join("one.bin"); let two=dir.path().join("two.bin");
        let bytes=vec![37u8;16*1024*1024]; fs::write(&one,&bytes).unwrap(); fs::write(&two,b"new payload").unwrap();
        let mut server=Server::start_limited(Ipv4Addr::LOCALHOST,0,vec![Source {path:one,fd:None,name:"one.bin".into()}],Some(1)).unwrap();
        let address=server.state.address;let session=server.state.session_id.clone(); let first=manifest(&server.state)["files"][0]["id"].as_str().unwrap().to_owned();
        let mut client=TcpStream::connect(address).unwrap();client.set_read_timeout(Some(Duration::from_secs(3))).unwrap();
        client.write_all(format!("GET /files/{first} HTTP/1.1\r\nHost: {address}\r\n\r\n").as_bytes()).unwrap();
        let mut headers=Vec::new(); let mut byte=[0u8;1];
        while !headers.ends_with(b"\r\n\r\n") {client.read_exact(&mut byte).unwrap();headers.push(byte[0]);}
        assert_eq!(server.snapshot()["activeDownloads"],1);
        let attempt=server.snapshot()["receivers"][0]["files"][0]["attempt"].clone();
        server.append(vec![Source {path:two,fd:None,name:"new & file.bin".into()}]).unwrap();
        assert_eq!(server.snapshot()["activeDownloads"],1); assert_eq!(server.snapshot()["receivers"][0]["files"][0]["attempt"],attempt);
        let response=get(address,format!("GET /manifest.json?session={session} HTTP/1.1\r\nHost: {address}\r\n\r\n"));
        let body=response.windows(4).position(|b|b==b"\r\n\r\n").unwrap()+4;
        let listing:Value=serde_json::from_slice(&response[body..]).unwrap();
        assert_eq!(listing["revision"],1);assert_eq!(listing["files"].as_array().unwrap().len(),2);assert_eq!(listing["files"][0]["id"],first);
        let second=listing["files"][1]["id"].as_str().unwrap();
        let refused=get_as(server.state.clone(),Ipv4Addr::new(192,168,49,3),format!("GET /manifest.json?session={session} HTTP/1.1\r\nHost: {address}\r\n\r\n"));
        assert!(refused.starts_with(b"HTTP/1.1 403"));
        let new_data=get(address,format!("GET /files/{second} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=4-10\r\n\r\n"));
        assert!(new_data.starts_with(b"HTTP/1.1 206"));assert!(new_data.ends_with(b"payload"));
        let mut received=Vec::new();client.read_to_end(&mut received).unwrap();assert_eq!(received,bytes);
        for _ in 0..100 {if server.snapshot()["activeDownloads"]==0 {break;} thread::sleep(Duration::from_millis(5));}
        assert_eq!(server.snapshot()["receivers"][0]["files"][0]["bytes"],bytes.len());
        assert_eq!(server.snapshot()["receivers"][0]["files"][0]["attempt"],attempt);
        let resumed=get(address,format!("GET /files/{first} HTTP/1.1\r\nHost: {address}\r\nRange: bytes=100-199\r\n\r\n"));assert!(resumed.ends_with(&bytes[100..200]));
        assert_eq!(server.state.address,address);server.stop().unwrap();
    }
    #[test]
    fn failed_append_is_atomic_and_manifest_tracks_removal_and_live_presence() {
        let dir=tempfile::tempdir().unwrap();let path=dir.path().join("one.bin");fs::write(&path,b"first").unwrap();
        let source=|| Source {path:path.clone(),fd:None,name:"one.bin".into()};
        let mut server=Server::start(Ipv4Addr::LOCALHOST,0,vec![source()]).unwrap();let before=manifest(&server.state);
        assert!(server.append(vec![source(),Source {path:dir.path().join("missing"),fd:None,name:"missing.bin".into()}]).is_err());
        assert_eq!(manifest(&server.state),before);
        let address=server.state.address;let session=server.state.session_id.clone();get(address,format!("GET /manifest.json?session={session} HTTP/1.1\r\nHost: {address}\r\n\r\n"));
        assert_eq!(server.snapshot()["receivers"][0]["connected"],true);
        server.state.receivers.lock().unwrap().values_mut().next().unwrap().last_seen=Some(Instant::now()-Duration::from_secs(7));
        assert_eq!(server.snapshot()["receivers"][0]["connected"],false);
        server.remove(&source()).unwrap();let after=manifest(&server.state);assert_eq!(after["revision"],1);assert!(after["files"].as_array().unwrap().is_empty());
        server.stop().unwrap();
    }
}
