use super::*;
use tempfile::TempDir;

fn stage8_instance(name: &str) -> (Arc<Core>, TempDir) {
    let dir = tempfile::tempdir().unwrap();
    let config: Config = serde_json::from_value(json!({
        "name":name,"download_dir":dir.path().join("downloads"),"state_dir":dir.path().join("state"),
        "port":0,"discovery_port":0,"streaming_hash":true,"discard_partials":true
    })).unwrap();
    (Core::start(config).unwrap(), dir)
}

#[test]
fn system_device_name_refresh_preserves_identity_and_updates_handshake_and_probe() {
    let (core, _dir) = stage8_instance("HarmonyOS · 设备");
    let before = core.current_info();
    assert_eq!(core.call(json!({"op":"set_name","name":"HarmonyOS · 测试手机"}))["ok"], true);
    let snapshot = core.call(json!({"op":"snapshot"}));
    assert_eq!(snapshot["info"]["name"], "HarmonyOS · 测试手机");
    assert_eq!(snapshot["info"]["fingerprint"], before.fingerprint);
    assert_eq!(snapshot["info"]["id"], before.id);
    let pair = core.call(json!({"op":"pair_begin"}));
    assert_eq!(pair["hello"]["name"], "HarmonyOS · 测试手机");
    let probe = core.call(json!({"op":"probe","host":"127.0.0.1","port":before.port}));
    assert_eq!(probe["peer"]["name"], "HarmonyOS · 测试手机");
    for invalid in ["".to_string(), " \t".to_string(), "bad\nname".to_string(), "名".repeat(43)] {
        assert_eq!(core.call(json!({"op":"set_name","name":invalid}))["ok"], false);
        assert_eq!(core.current_info().name, "HarmonyOS · 测试手机");
    }
    core.stop();
}

#[test]
fn streaming_integrity_roundtrip_empty_batch_collision_and_persistent_history() {
    let (sender, source_dir) = stage8_instance("sender");
    let (receiver, _receiver_dir) = stage8_instance("receiver");
    let source = source_dir.path().join("large.bin");
    let empty = source_dir.path().join("empty");
    let data = vec![91; CHUNK * 3 + 13];
    fs::write(&source, &data).unwrap(); fs::write(&empty, []).unwrap();
    fs::write(receiver.config.download_dir.join("data.bin"), b"existing").unwrap();
    let sent = sender.call(json!({"op":"send","peer":{"host":"127.0.0.1","port":receiver.info.port,"fingerprint":receiver.info.fingerprint},
        "verificationCode":receiver.info.verification_code,"files":[{"name":"data.bin","path":source},{"name":"empty","path":empty}]}));
    assert_eq!(sent["ok"], true, "{sent}");
    let incoming = wait_for(&receiver, |s| s["transfers"][0]["state"] == "awaiting_accept");
    assert!(!receiver.config.download_dir.join(".sharelink-partials").exists());
    receiver.call(json!({"op":"accept","id":incoming["transfers"][0]["id"]}));
    wait_for(&sender, |s| s["transfers"][0]["state"] == "completed");
    let completed = wait_for(&receiver, |s| s["transfers"][0]["state"] == "completed");
    assert_eq!(fs::read(receiver.config.download_dir.join("data.bin")).unwrap(), b"existing");
    let path = completed["transfers"][0]["files"][0]["path"].as_str().unwrap();
    assert_eq!(fs::read(path).unwrap(), data);
    assert_eq!(fs::metadata(receiver.config.download_dir.join("empty")).unwrap().len(), 0);
    let config = receiver.config.clone(); receiver.stop();
    let restarted = Core::start(config).unwrap();
    assert_eq!(restarted.call(json!({"op":"snapshot"}))["transfers"][0]["state"], "completed");
    assert_eq!(restarted.call(json!({"op":"clear_history"}))["ok"], true);
    assert_eq!(restarted.call(json!({"op":"snapshot"}))["transfers"].as_array().unwrap().len(), 0);
    assert!(Path::new(path).exists());
    sender.stop(); restarted.stop();
}

#[test]
fn small_file_transfers_keep_the_batch_rate_across_verification() {
    const FILE_BYTES: u64 = 16 * 1024;
    for direction in ["receive", "send"] {
        let job = Job::new(direction, "peer", Vec::new(), "awaiting_accept");
        job.state("transferring");
        for file in 1..=120 {
            let completed = file * FILE_BYTES;
            job.progress(completed);
            job.state("verifying");
            if direction == "receive" {
                job.progress(completed);
            }
            job.state("transferring");
        }
        let view = job.view.lock().unwrap().clone();
        assert_eq!(view.bytes, 120 * FILE_BYTES);
        assert!(view.speed > 0, "{direction} batch lost its measured rate");
        job.finish(Ok(()));
        assert_eq!(job.view.lock().unwrap().speed, 0);
    }
}

#[test]
fn transfer_rate_counts_new_payload_only() {
    let job = Job::new("receive", "peer", Vec::new(), "awaiting_accept");
    job.state("transferring");
    job.sample.lock().unwrap().0 = Instant::now() - Duration::from_secs(1);
    job.initial_bytes.store(1024, Ordering::Relaxed);
    job.progress(1024);
    assert_eq!(job.view.lock().unwrap().speed, 0);

    job.progress(2048);
    let view = job.view.lock().unwrap();
    assert_eq!(view.bytes, 2048);
    assert!(view.speed > 0 && view.speed <= 1024);
}

#[test]
fn delete_one_history_entry_persists_without_removing_files_or_active_jobs() {
    let (sender, sources) = stage8_instance("sender");
    let (receiver, _receipts) = stage8_instance("receiver");
    let source = sources.path().join("kept.txt");
    fs::write(&source, b"keep source and receipt").unwrap();
    assert_eq!(sender.call(json!({"op":"send","peer":{"host":"127.0.0.1","port":receiver.info.port,"fingerprint":receiver.info.fingerprint},
        "verificationCode":receiver.info.verification_code,"files":[{"path":source,"name":"kept.txt"}]}))["ok"], true);
    let incoming = wait_for(&receiver, |snapshot| snapshot["transfers"][0]["state"] == "awaiting_accept");
    let id = incoming["transfers"][0]["id"].clone();
    receiver.call(json!({"op":"accept","id":id}));
    wait_for(&sender, |snapshot| snapshot["transfers"][0]["state"] == "completed");
    wait_for(&receiver, |snapshot| snapshot["transfers"][0]["state"] == "completed");
    let (held_stream, active_id) = offer(&receiver, b"pending", None);
    assert_eq!(receiver.call(json!({"op":"delete_history","id":active_id}))["ok"], false);
    assert_eq!(receiver.call(json!({"op":"delete_history","id":"unknown"}))["ok"], false);
    assert_eq!(receiver.call(json!({"op":"delete_history","id":id}))["ok"], true);
    let snapshot = receiver.call(json!({"op":"snapshot"}));
    assert_eq!(snapshot["transfers"].as_array().unwrap().len(), 1);
    assert_eq!(snapshot["transfers"][0]["id"], active_id);
    assert_eq!(fs::read(&source).unwrap(), b"keep source and receipt");
    let receipt = receiver.config.download_dir.join("kept.txt");
    assert_eq!(fs::read(&receipt).unwrap(), b"keep source and receipt");
    drop(held_stream);
    let config = receiver.config.clone();
    receiver.stop();
    let restarted = Core::start(config).unwrap();
    assert!(!restarted.call(json!({"op":"snapshot"}))["transfers"].as_array().unwrap().iter().any(|task| task["id"] == id));
    assert_eq!(fs::read(receipt).unwrap(), b"keep source and receipt");
    sender.stop(); restarted.stop();
}

#[test]
fn streaming_bad_digest_and_cancelled_partial_are_never_published() {
    let (receiver, _dir) = stage8_instance("receiver");
    for corrupt in [true, false] {
        let mut stream = connect("127.0.0.1", receiver.info.port, Some(receiver.info.fingerprint.clone())).unwrap();
        let index = receiver.call(json!({"op":"snapshot"}))["transfers"].as_array().unwrap().len();
        write_json(&mut stream, &json!({"op":"offer","version":2,"streamingHash":true,"sender":"sender","files":[{"name":"bad.bin","size":CHUNK * 2,"hash":""}]})).unwrap();
        let snapshot = wait_for(&receiver, |s| s["transfers"][index]["state"] == "awaiting_accept");
        receiver.call(json!({"op":"accept","id":snapshot["transfers"][index]["id"]}));
        read_json(&mut stream).unwrap(); read_json(&mut stream).unwrap();
        write_json(&mut stream, &json!({"offset":0})).unwrap();
        stream.write_all(&vec![3; CHUNK]).unwrap(); stream.flush().unwrap();
        if corrupt {
            stream.write_all(&vec![3; CHUNK]).unwrap();
            write_json(&mut stream, &json!({"hash":"0".repeat(64)})).unwrap();
        } else { stream.sock.shutdown(Shutdown::Both).unwrap(); }
        drop(stream);
        wait_for(&receiver, |s| s["transfers"][index]["state"] == "failed");
        assert!(!receiver.config.download_dir.join("bad.bin").exists());
        assert_eq!(fs::read_dir(receiver.config.download_dir.join(".sharelink-partials")).unwrap().count(), 0);
    }
    receiver.stop();
}

#[test]
fn clear_history_keeps_active_job_and_start_aborts_journalled_job() {
    let (receiver, _dir) = stage8_instance("receiver");
    let (stream, id) = offer(&receiver, b"held", None);
    assert_eq!(receiver.call(json!({"op":"clear_history"}))["ok"], true);
    assert_eq!(receiver.call(json!({"op":"snapshot"}))["transfers"][0]["id"], id);
    let config = receiver.config.clone();
    // The persisted active journal is what survives an abrupt process kill.
    let journal = fs::read(config.state_dir.join("transfer-history.json")).unwrap();
    receiver.stop(); drop(stream);
    fs::write(config.state_dir.join("transfer-history.json"), journal).unwrap();
    let partials = config.download_dir.join(".sharelink-partials"); fs::create_dir_all(&partials).unwrap();
    fs::write(partials.join("abc-123.part"), b"unfinished").unwrap();
    fs::write(partials.join("user-document.txt"), b"unrelated").unwrap();
    let restarted = Core::start(config).unwrap();
    let snapshot = restarted.call(json!({"op":"snapshot"}));
    assert_eq!(snapshot["transfers"][0]["state"], "failed");
    assert!(!partials.join("abc-123.part").exists());
    assert!(partials.join("user-document.txt").exists());
    restarted.stop();
}

#[test]
fn unloaded_lan_blocks_sockets_until_direct_binding_and_after_cleanup() {
    let dir = tempfile::tempdir().unwrap();
    let core = Core::start(Config {
        device_type: unknown_device_type(),
        name: "Direct-only".into(), download_dir: dir.path().join("downloads"),
        state_dir: dir.path().join("state"), port: 0, discovery_port: 53318,
        nearby_only: false, streaming_hash: false, discard_partials: false, partial_dir: None, transport_modules: Some(vec!["nearby-direct".into()]),
    }).unwrap();
    assert!(core.discovery.is_none());
    assert_eq!(core.current_info().addresses, Vec::<String>::new());
    assert_eq!(core.call(json!({"op":"probe","host":"127.0.0.1","port":core.info.port}))["ok"], false);
    assert!(connect("127.0.0.1", core.info.port, None).is_err());
    assert_eq!(core.call(json!({"op":"direct_bind","address":"192.0.2.3"}))["ok"], false);
    assert_eq!(core.call(json!({"op":"direct_bind","address":"127.0.0.1"}))["ok"], true);
    assert_eq!(core.call(json!({"op":"probe","host":"127.0.0.1","port":core.info.port}))["ok"], true);
    assert_eq!(core.current_info().addresses, vec!["127.0.0.1"]);
    assert!(core.transport.admit("127.0.0.2".parse().unwrap()).is_err());
    assert_eq!(core.call(json!({"op":"direct_bind","address":null}))["ok"], true);
    assert_eq!(core.call(json!({"op":"probe","host":"127.0.0.1","port":core.info.port}))["ok"], false);
    assert!(connect("127.0.0.1", core.info.port, None).is_err());
    core.stop();
}

#[test]
fn transport_modules_are_explicit_and_disabled_modules_cannot_bind() {
    let dir = tempfile::tempdir().unwrap();
    let config = |modules| Config {
        device_type: unknown_device_type(),
        name: "policy".into(), download_dir: dir.path().join("other"), state_dir: dir.path().join("other-state"),
        port: 0, discovery_port: 0, nearby_only: false, streaming_hash: false, discard_partials: false, partial_dir: None, transport_modules: Some(modules),
    };
    assert!(transport::TransportPolicy::new(&config(vec!["unknown".into()])).is_err());
    let empty = transport::TransportPolicy::new(&config(vec![])).unwrap();
    assert!(!empty.lan_enabled && empty.source().is_err());
    let lan = transport::TransportPolicy::new(&config(vec!["lan".into()])).unwrap();
    assert!(lan.lan_enabled && lan.source().unwrap().is_none());
    assert!(lan.bind(Some(Ipv4Addr::LOCALHOST)).is_err());
    lan.paused.store(true, Ordering::Release);
    assert!(lan.source().is_err());
    assert!(lan.admit(IpAddr::V4(Ipv4Addr::LOCALHOST)).is_err());
    lan.paused.store(false, Ordering::Release);
    assert!(lan.source().unwrap().is_none());
}

fn instance(name: &str) -> (Arc<Core>, TempDir) {
    let dir = tempfile::tempdir().unwrap();
    let core = Core::start(Config {
        device_type: unknown_device_type(),
        name: name.into(),
        download_dir: dir.path().join("downloads"),
        state_dir: dir.path().join("state"),
        port: 0,
        discovery_port: 0,
        nearby_only: false, streaming_hash: false, discard_partials: false, partial_dir: None,
        transport_modules: None,
    })
    .unwrap();
    (core, dir)
}
fn wait_for<F: Fn(&Value) -> bool>(core: &Arc<Core>, condition: F) -> Value {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        let s = core.call(json!({"op":"snapshot"}));
        if condition(&s) {
            return s;
        }
        assert!(Instant::now() < deadline, "Timed out: {s}");
        thread::sleep(Duration::from_millis(20));
    }
}
fn offer(
    core: &Arc<Core>,
    data: &[u8],
    hash: Option<String>,
) -> (StreamOwned<ClientConnection, TcpStream>, String) {
    let mut stream = connect(
        "127.0.0.1",
        core.info.port,
        Some(core.info.fingerprint.clone()),
    )
    .unwrap();
    write_json(&mut stream,&json!({"op":"offer","version":1,"sender":"sender","files":[{"name":"sample.bin","size":data.len(),"hash":hash.unwrap_or_else(||blake3::hash(data).to_hex().to_string())}]})).unwrap();
    let state = wait_for(core, |s| {
        s["transfers"]
            .as_array()
            .unwrap()
            .iter()
            .any(|t| t["state"] == "awaiting_accept")
    });
    let id = state["transfers"].as_array().unwrap().last().unwrap()["id"]
        .as_str()
        .unwrap()
        .to_owned();
    (stream, id)
}

#[cfg(windows)]
#[test]
fn windows_publish_across_volumes_keeps_existing_file() {
    let Some(root) = std::env::var_os("SHARELINK_CROSS_VOLUME_TEST_ROOT") else { return; };
    let source_dir = tempfile::tempdir().unwrap();
    let destination_dir = tempfile::tempdir_in(root).unwrap();
    assert_ne!(source_dir.path().components().next(), destination_dir.path().components().next(), "This check requires different volumes");
    let part = source_dir.path().join("owned.part");
    let content = vec![0x5a; 65537];
    fs::write(&part, &content).unwrap();
    fs::write(destination_dir.path().join("saved.bin"), b"existing user file").unwrap();
    let saved = publish_no_replace(&part, destination_dir.path(), "saved.bin").unwrap();
    assert_eq!(saved.file_name().unwrap(), "saved (1).bin");
    assert_eq!(fs::read(saved).unwrap(), content);
    assert_eq!(fs::read(destination_dir.path().join("saved.bin")).unwrap(), b"existing user file");
    assert!(!part.exists());
}

#[test]
fn path_safety_and_bounded_frames() {
    for name in [
        "../x",
        "a\\b",
        "/absolute",
        "CON.txt",
        "NUL",
        "com1.mp4",
        "LPT³.txt",
        "con .txt",
        "a:b",
        ".sharelink-partials",
        "x.",
        "x ",
        "a\n.txt",
    ] {
        assert!(safe_name(name).is_err(), "{name}");
    }
    for name in ["中文📁.txt", "hello world.mp4", "CONSOLE.txt", "empty"] {
        assert!(safe_name(name).is_ok(), "{name}");
    }
    assert!(read_json(&mut &((CHUNK as u32 + 1).to_be_bytes())[..]).is_err());
    assert!(read_json(&mut &[0u8, 0, 0, 5, b'{'][..]).is_err());
}

#[test]
fn wrong_pin_is_rejected_by_tls() {
    let (c, _d) = instance("receiver");
    assert!(connect("127.0.0.1", c.info.port, Some("0".repeat(64))).is_err());
    c.stop();
}

#[test]
fn unaccepted_offer_never_creates_partial() {
    let (c, _d) = instance("receiver");
    let (stream, id) = offer(&c, b"secret", None);
    assert!(!c.config.download_dir.join(".sharelink-partials").exists());
    assert_eq!(c.call(json!({"op":"reject","id":id}))["ok"], true);
    drop(stream);
    wait_for(&c, |s| s["transfers"][0]["state"] == "rejected");
    assert!(!c.config.download_dir.join("sample.bin").exists());
    c.stop();
}

#[test]
fn truncated_file_resumes_and_corrupt_prefix_restarts() {
    let (c, _d) = instance("receiver");
    let data = vec![7u8; CHUNK * 3 + 99];
    let (mut stream, id) = offer(&c, &data, None);
    c.call(json!({"op":"accept","id":id}));
    check_response(read_json(&mut stream).unwrap()).unwrap();
    let ready = read_json(&mut stream).unwrap();
    assert_eq!(ready["offset"], 0);
    write_json(&mut stream, &json!({"offset":0})).unwrap();
    stream.write_all(&data[..CHUNK]).unwrap();
    stream.flush().unwrap();
    stream.sock.shutdown(Shutdown::Both).unwrap();
    drop(stream);
    wait_for(&c, |s| s["transfers"][0]["state"] == "failed");
    assert!(!c.config.download_dir.join("sample.bin").exists());
    let hash = blake3::hash(&data).to_hex().to_string();
    let part = c
        .config
        .download_dir
        .join(".sharelink-partials")
        .join(format!("{hash}-{}.part", data.len()));
    assert_eq!(fs::metadata(&part).unwrap().len(), CHUNK as u64);
    // Persisted partial bytes are untrusted; the sender compares the receiver prefix digest.
    {
        let mut f = OpenOptions::new().write(true).open(&part).unwrap();
        f.write_all(&[9]).unwrap();
        f.sync_all().unwrap();
    }
    let (sender, source_dir) = instance("sender");
    let source = source_dir.path().join("sample.bin");
    fs::write(&source, &data).unwrap();
    let response=sender.call(json!({"op":"send","peer":{"host":"127.0.0.1","port":c.info.port,"fingerprint":c.info.fingerprint},"verificationCode":c.info.verification_code,"files":[{"path":source,"name":"sample.bin"}]}));
    assert_eq!(response["ok"], true, "{response}");
    let incoming = wait_for(&c, |s| s["transfers"][1]["state"] == "awaiting_accept");
    c.call(json!({"op":"accept","id":incoming["transfers"][1]["id"]}));
    wait_for(&sender, |s| s["transfers"][0]["state"] == "completed");
    assert_eq!(
        fs::read(c.config.download_dir.join("sample.bin")).unwrap(),
        data
    );
    sender.stop();
    c.stop();
}

#[test]
fn corruption_is_not_committed() {
    let (c, _d) = instance("receiver");
    let (mut stream, id) = offer(
        &c,
        b"hello",
        Some(blake3::hash(b"world").to_hex().to_string()),
    );
    c.call(json!({"op":"accept","id":id}));
    check_response(read_json(&mut stream).unwrap()).unwrap();
    read_json(&mut stream).unwrap();
    write_json(&mut stream, &json!({"offset":0})).unwrap();
    stream.write_all(b"hello").unwrap();
    stream.flush().unwrap();
    assert_eq!(read_json(&mut stream).unwrap()["ok"], false);
    wait_for(&c, |s| s["transfers"][0]["state"] == "failed");
    assert!(!c.config.download_dir.join("sample.bin").exists());
    c.stop();
}

#[test]
fn collision_preserves_existing_file() {
    let dir = tempfile::tempdir().unwrap();
    let part = dir.path().join("part");
    fs::write(&part, b"new").unwrap();
    fs::write(dir.path().join("test.txt"), b"old").unwrap();
    let destination = publish_no_replace(&part, dir.path(), "test.txt").unwrap();
    assert_eq!(destination.file_name().unwrap(), "test (1).txt");
    assert_eq!(fs::read(dir.path().join("test.txt")).unwrap(), b"old");
    assert_eq!(fs::read(destination).unwrap(), b"new");
}

#[test]
fn cancellation_propagates_while_awaiting_acceptance() {
    let (c, _d) = instance("receiver");
    let (stream, _) = offer(&c, b"hello", None);
    stream.sock.shutdown(Shutdown::Both).unwrap();
    wait_for(&c, |s| s["transfers"][0]["state"] == "failed");
    c.stop();
}

#[test]
fn exclusive_commit_fallback_preserves_existing_file_and_source() {
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("part");
    let destination = dir.path().join("result.txt");
    fs::write(&source, b"new data").unwrap();
    fs::write(&destination, b"existing data").unwrap();
    assert_eq!(copy_no_replace(&source, &destination).unwrap_err().kind(), std::io::ErrorKind::AlreadyExists);
    assert_eq!(fs::read(&destination).unwrap(), b"existing data");
    assert_eq!(fs::read(&source).unwrap(), b"new data");
    let fresh = dir.path().join("fresh.txt");
    copy_no_replace(&source, &fresh).unwrap();
    assert_eq!(fs::read(fresh).unwrap(), b"new data");
    assert!(source.exists());
}

#[test]
fn concurrent_exclusive_commit_fallback_has_one_winner() {
    let dir = tempfile::tempdir().unwrap();
    let destination = dir.path().join("result.txt");
    let sources = [dir.path().join("a"), dir.path().join("b")];
    fs::write(&sources[0], b"first").unwrap();
    fs::write(&sources[1], b"second").unwrap();
    let gate = Arc::new(std::sync::Barrier::new(2));
    let threads: Vec<_> = sources.into_iter().map(|source| {
        let destination = destination.clone(); let gate = gate.clone();
        thread::spawn(move || { gate.wait(); copy_no_replace(&source, &destination) })
    }).collect();
    let results: Vec<_> = threads.into_iter().map(|worker| worker.join().unwrap()).collect();
    assert_eq!(results.iter().filter(|r| r.is_ok()).count(), 1);
    assert_eq!(results.iter().find(|r| r.is_err()).unwrap().as_ref().unwrap_err().kind(), std::io::ErrorKind::AlreadyExists);
    let bytes = fs::read(destination).unwrap();
    assert!(bytes == b"first" || bytes == b"second");
}

#[test]
fn path_cost_requires_both_benefit_thresholds_and_preserves_unknown() {
    let base = json!({"remainingBytes":2_000_000_000u64,"currentBytesPerSecond":40_000_000,
        "candidateBytesPerSecond":90_000_000,"setupSeconds":8,"keepCurrentNetwork":false});
    let large = link::select_path(&base).unwrap();
    assert_eq!(large["choice"], "candidate");
    assert_eq!(large["currentEstimatedSeconds"], 50.0);
    assert_eq!(large["candidateRateSource"], "caller-estimate");
    let mut r = base.clone();
    r["remainingBytes"] = json!(20_000_000);
    assert_eq!(link::select_path(&r).unwrap()["choice"], "current");
    r = base.clone();
    r["keepCurrentNetwork"] = json!(true);
    assert_eq!(
        link::select_path(&r).unwrap()["reason"],
        "keep-current-network"
    );
    // Saving more than three seconds alone is insufficient for a long task.
    r = json!({"remainingBytes":1000,"currentBytesPerSecond":10,"candidateBytesPerSecond":11,"setupSeconds":0});
    assert_eq!(link::select_path(&r).unwrap()["choice"], "current");
    // Saving over twenty percent alone is insufficient for a small task.
    r = json!({"remainingBytes":1000,"currentBytesPerSecond":1000,"candidateBytesPerSecond":10000,"setupSeconds":0});
    assert_eq!(link::select_path(&r).unwrap()["choice"], "current");
    r["currentBytesPerSecond"] = Value::Null;
    r["candidateBytesPerSecond"] = Value::Null;
    let unknown = link::select_path(&r).unwrap();
    assert_eq!(unknown["choice"], "unavailable");
    assert!(unknown["currentEstimatedSeconds"].is_null());
    r["currentBytesPerSecond"] = json!(0);
    assert!(link::select_path(&r).is_err());
}

#[test]
fn reports_reach_tls_probe_but_not_untrusted_broadcast_info() {
    let (a, _ad) = instance("client");
    let (b, _bd) = instance("server");
    let report = json!({"source":"platform-api","standard":null,"rxMbps":1200,"role":"sta"});
    assert_eq!(
        b.call(json!({"op":"report_link","report":report}))["ok"],
        true
    );
    let probe = a.call(json!({"op":"probe","host":"127.0.0.1","port":b.info.port}));
    assert_eq!(probe["peer"]["linkReport"], report);
    assert!(probe["info"]["addresses"].is_array());
    assert!(b.info.link_report.is_null());
    let too_large = json!({"blob":"x".repeat(16 * 1024)});
    assert_eq!(
        b.call(json!({"op":"report_link","report":too_large}))["ok"],
        false
    );
    a.stop();
    b.stop();
}

#[test]
fn directional_tls_probes_are_bounded_and_do_not_create_transfers() {
    let (a, _ad) = instance("client");
    let (b, _bd) = instance("server");
    for direction in ["upload", "download"] {
        let started = Instant::now();
        let measured = a.call(json!({"op":"measure","peer":{"host":"127.0.0.1","port":b.info.port,
            "fingerprint":b.info.fingerprint},"verificationCode":b.info.verification_code,"direction":direction}));
        assert_eq!(measured["ok"], true, "{measured}");
        assert!(measured["bytes"].as_u64().unwrap() <= 16 * 1024 * 1024);
        assert!(measured["bytesPerSecond"].as_u64().unwrap() > 0);
        assert_eq!(measured["direction"], direction);
        assert_eq!(measured["storageMeasured"], false);
        assert!(started.elapsed() < Duration::from_secs(5));
        assert!(b.jobs.lock().unwrap().is_empty());
    }
    a.stop();
    b.stop();
}

#[test]
fn probe_rejects_oversized_frames_without_allocating_the_claimed_size() {
    let mut bytes = Vec::new();
    bytes.extend_from_slice(&((64 * 1024 + 1) as u32).to_be_bytes());
    assert!(link::test_read_probe(&mut &bytes[..]).is_err());
}

fn paired(a: &Arc<Core>, b: &Arc<Core>) -> (String, String) {
    let aa = a.call(json!({"op":"pair_begin"}));
    let bb = b.call(json!({"op":"pair_begin"}));
    let fa = a.call(json!({"op":"pair_finish","pairId":aa["pairId"],"hello":bb["hello"]}));
    let fb = b.call(json!({"op":"pair_finish","pairId":bb["pairId"],"hello":aa["hello"]}));
    assert_eq!(fa["ok"], true, "{fa}");
    assert_eq!(fa["sas"], fb["sas"]);
    assert_eq!(fa["sas"].as_str().unwrap().len(), 6);
    (
        aa["pairId"].as_str().unwrap().into(),
        bb["pairId"].as_str().unwrap().into(),
    )
}

#[test]
fn pairing_authenticates_control_and_needs_each_local_confirmation() {
    let (a, _ad) = instance("client");
    let (b, _bd) = instance("server");
    let (pa, pb) = paired(&a, &b);
    assert_eq!(
        a.call(json!({"op":"pair_encrypt","pairId":pa,"payload":{}}))["ok"],
        false
    );
    a.call(json!({"op":"pair_confirm","pairId":pa,"confirmed":true}));
    let envelope = a.call(
        json!({"op":"pair_encrypt","pairId":pa,"payload":{"password":"sensitive","name":"测试"}}),
    )["envelope"]
        .clone();
    assert!(!envelope.to_string().contains("sensitive"));
    assert_eq!(
        b.call(json!({"op":"pair_decrypt","pairId":pb,"envelope":envelope}))["ok"],
        false
    );
    b.call(json!({"op":"pair_confirm","pairId":pb,"confirmed":true}));
    let decoded = b.call(json!({"op":"pair_decrypt","pairId":pb,"envelope":envelope}));
    assert_eq!(decoded["payload"]["password"], "sensitive");
    assert_eq!(
        b.call(json!({"op":"pair_decrypt","pairId":pb,"envelope":envelope}))["ok"],
        false
    );
    let mut corrupted = b
        .call(json!({"op":"pair_encrypt","pairId":pb,"payload":{"status":"ready"}}))["envelope"]
        .clone();
    let original = corrupted.clone();
    let bytes = hex::decode(corrupted["ciphertext"].as_str().unwrap()).unwrap();
    let mut altered = bytes;
    altered[0] ^= 1;
    corrupted["ciphertext"] = json!(hex::encode(altered));
    assert_eq!(
        a.call(json!({"op":"pair_decrypt","pairId":pa,"envelope":corrupted}))["ok"],
        false
    );
    // A failed authentication must not consume the receive sequence.
    assert_eq!(
        a.call(json!({"op":"pair_decrypt","pairId":pa,"envelope":original}))["payload"]["status"],
        "ready"
    );
    a.stop();
    b.stop();
}

#[test]
fn pairing_transcript_binds_tls_identity_and_expires() {
    let (a, _ad) = instance("client");
    let (b, _bd) = instance("server");
    let aa = a.call(json!({"op":"pair_begin"}));
    let bb = b.call(json!({"op":"pair_begin"}));
    let mut changed = bb["hello"].clone();
    changed["fingerprint"] = json!("0".repeat(64));
    let fa = a.call(json!({"op":"pair_finish","pairId":aa["pairId"],"hello":changed}));
    let fb = b.call(json!({"op":"pair_finish","pairId":bb["pairId"],"hello":aa["hello"]}));
    // Compare the full transcript binding, avoiding a flaky 1-in-1M SAS collision assertion.
    assert_ne!(fa["binding"], fb["binding"]);
    b.pairings.lock().unwrap().expire_all_for_test();
    assert_eq!(
        b.call(json!({"op":"pair_confirm","pairId":bb["pairId"],"confirmed":true}))["ok"],
        false
    );
    a.stop();
    b.stop();
}

#[test]
fn phone_server_pull_requires_acceptance_and_consumes_token_once() {
    let (phone, dir) = instance("phone server");
    let (pc, _pd) = instance("PC client");
    let source = dir.path().join("picture.bin");
    fs::write(&source, vec![42; CHUNK + 19]).unwrap();
    let prepared =
        phone.call(json!({"op":"prepare_pull","files":[{"path":source,"name":"picture.bin"}]}));
    assert_eq!(prepared["ok"], true);
    let request = json!({"op":"pull","peer":{"host":"127.0.0.1","port":phone.info.port,"fingerprint":phone.info.fingerprint},
        "verificationCode":phone.info.verification_code,"token":prepared["token"]});
    let received = pc.call(request.clone());
    assert_eq!(received["ok"], true);
    wait_for(&pc, |s| s["transfers"][0]["state"] == "awaiting_accept");
    assert!(!pc.config.download_dir.join("picture.bin").exists());
    pc.call(json!({"op":"accept","id":received["id"]}));
    wait_for(&pc, |s| s["transfers"][0]["state"] == "completed");
    wait_for(&phone, |s| s["transfers"][0]["state"] == "completed");
    assert_eq!(
        fs::read(pc.config.download_dir.join("picture.bin")).unwrap(),
        fs::read(&source).unwrap()
    );
    let second = pc.call(request);
    assert_eq!(second["ok"], true);
    wait_for(&pc, |s| s["transfers"][1]["state"] == "failed");
    phone.stop();
    pc.stop();
}

#[test]
fn device_form_factor_crosses_offer_acceptance_and_history_with_legacy_defaults() {
    let source_dir = tempfile::tempdir().unwrap();
    let target_dir = tempfile::tempdir().unwrap();
    let make = |dir: &Path, name: &str, kind: &str| Core::start(serde_json::from_value(json!({"name":name,"device_type":kind,"download_dir":dir.join("downloads"),"state_dir":dir.join("state"),"port":0,"discovery_port":0})).unwrap()).unwrap();
    let sender = make(source_dir.path(), "sender", "tablet");
    let receiver = make(target_dir.path(), "receiver", "computer");
    let source = source_dir.path().join("type.txt");fs::write(&source,b"device form factor").unwrap();
    assert_eq!(sender.info.device_type,"tablet");
    assert_eq!(sender.call(json!({"op":"send","peer":{"host":"127.0.0.1","port":receiver.info.port,"fingerprint":receiver.info.fingerprint},"verificationCode":receiver.info.verification_code,"files":[{"name":"type.txt","path":source}]}))["ok"],true);
    let incoming = wait_for(&receiver, |s| s["transfers"][0]["state"] == "awaiting_accept");
    assert_eq!(incoming["transfers"][0]["peerDeviceType"],"tablet");
    receiver.call(json!({"op":"accept","id":incoming["transfers"][0]["id"]}));
    let sent=wait_for(&sender,|s|s["transfers"][0]["state"]=="completed");
    let received=wait_for(&receiver,|s|s["transfers"][0]["state"]=="completed");
    assert_eq!(sent["transfers"][0]["peerDeviceType"],"computer");
    assert_eq!(received["transfers"][0]["peerDeviceType"],"tablet");
    let mut old=received["transfers"][0].clone();old.as_object_mut().unwrap().remove("peerDeviceType");
    assert_eq!(serde_json::from_value::<Transfer>(old).unwrap().peer_device_type,"unknown");
    let old_offer:Offer=serde_json::from_value(json!({"sender":"old client","files":[]})).unwrap();assert_eq!(old_offer.sender_device_type,"unknown");
    let config=receiver.config.clone();receiver.stop();let restarted=Core::start(config).unwrap();
    assert_eq!(restarted.call(json!({"op":"snapshot"}))["transfers"][0]["peerDeviceType"],"tablet");
    sender.stop();restarted.stop();
}

// Exercise both connection roles: push sender connects, pull receiver connects.
// A large sparse source leaves time to cancel while actual TLS bytes are moving.
fn explicit_cancel_roundtrip(pull: bool, cancel_sender: bool, before_accept: bool) {
    let (sender, sources) = stage8_instance("cancel sender");
    let (receiver, _receipts) = stage8_instance("cancel receiver");
    let source = sources.path().join("cancel.bin");
    File::create(&source).unwrap().set_len(256 * 1024 * 1024).unwrap();
    let files = json!([{"path":source,"name":"cancel.bin"}]);
    let sent = if pull {
        let offered = sender.call(json!({"op":"prepare_pull","files":files}));
        assert_eq!(offered["ok"], true, "{offered}");
        let pulled = receiver.call(json!({"op":"pull","peer":{"host":"127.0.0.1","port":sender.info.port,
            "fingerprint":sender.info.fingerprint},"verificationCode":sender.info.verification_code,"token":offered["token"]}));
        assert_eq!(pulled["ok"], true, "{pulled}");
        offered
    } else {
        sender.call(json!({"op":"send","peer":{"host":"127.0.0.1","port":receiver.info.port,
            "fingerprint":receiver.info.fingerprint},"verificationCode":receiver.info.verification_code,"files":files}))
    };
    assert_eq!(sent["ok"], true, "{sent}");
    let incoming = wait_for(&receiver, |s| s["transfers"][0]["state"] == "awaiting_accept");
    if !before_accept {
        assert_eq!(receiver.call(json!({"op":"accept","id":incoming["transfers"][0]["id"]}))["ok"], true);
        wait_for(&receiver, |s| s["transfers"][0]["bytes"].as_u64().unwrap_or(0) >= CHUNK as u64);
    } else {
        // Wait until the upgraded pre-acceptance handshake has been consumed.
        let until = Instant::now() + Duration::from_secs(5);
        while sender.jobs.lock().unwrap()[0].cancel_peer.lock().unwrap().is_none() {
            assert!(Instant::now() < until, "No cancellation handshake");
            thread::sleep(Duration::from_millis(5));
        }
    }
    let cancelling = if cancel_sender { &sender } else { &receiver };
    let id = cancelling.call(json!({"op":"snapshot"}))["transfers"][0]["id"].clone();
    let result = cancelling.call(json!({"op":"cancel","id":id}));
    assert_eq!(result["ok"], true, "{result}");
    for core in [&sender, &receiver] {
        wait_for(core, |s| s["transfers"][0]["state"] == "cancelled" && s["transfers"][0]["finishedAt"].as_u64().unwrap_or(0) > 0);
        assert!(core.call(json!({"op":"snapshot"}))["transfers"][0].get("error").is_none());
    }
    assert!(!receiver.config.download_dir.join("cancel.bin").exists());
    let partials = receiver.config.partial_dir.as_ref().unwrap();
    if partials.exists() { assert_eq!(fs::read_dir(partials).unwrap().count(), 0); }
    sender.stop(); receiver.stop();
}
#[test] fn explicit_cancel_push_receiver_active() { explicit_cancel_roundtrip(false, false, false); }
#[test] fn explicit_cancel_push_sender_active() { explicit_cancel_roundtrip(false, true, false); }
#[test] fn explicit_cancel_pull_receiver_active() { explicit_cancel_roundtrip(true, false, false); }
#[test] fn explicit_cancel_pull_sender_active() { explicit_cancel_roundtrip(true, true, false); }
#[test] fn explicit_cancel_push_receiver_pending() { explicit_cancel_roundtrip(false, false, true); }
#[test] fn explicit_cancel_push_sender_pending() { explicit_cancel_roundtrip(false, true, true); }
#[test]
fn cancellation_requires_secret_capability_and_preserves_finished_tasks() {
    let (core, _dir) = stage8_instance("receiver");
    let (held, id) = offer(&core, b"pending", None);
    for token in ["bad".to_string(), "0".repeat(64)] {
        let mut stream = connect("127.0.0.1", core.info.port, Some(core.info.fingerprint.clone())).unwrap();
        write_json(&mut stream, &json!({"op":"cancel_transfer","token":token})).unwrap();
        let _ = read_json(&mut stream);
        assert_eq!(core.call(json!({"op":"snapshot"}))["transfers"][0]["state"], "awaiting_accept");
    }
    let job = core.jobs.lock().unwrap()[0].clone();
    job.state("completed");
    let mut stream = connect("127.0.0.1", core.info.port, Some(core.info.fingerprint.clone())).unwrap();
    write_json(&mut stream, &json!({"op":"cancel_transfer","token":job.cancel_token})).unwrap();
    let _ = read_json(&mut stream);
    assert_eq!(core.call(json!({"op":"snapshot"}))["transfers"][0]["state"], "completed");
    assert!(!job.cancelled.load(Ordering::Relaxed));
    drop(held); let _ = id; core.stop();
}

#[test]
fn delayed_explicit_cancel_arrives_before_transport_failure_releases_network() {
    let (core, _dir) = stage8_instance("delayed cancel");
    let job = Job::new("send", "peer", Vec::new(), "transferring");
    job.set_cancel_peer("127.0.0.1".into(), core.cancel_capability(&job)).unwrap();
    core.add_job(job.clone()).unwrap();
    let finishing = { let core=core.clone(); let job=job.clone(); thread::spawn(move || {
        core.finish_job(&job, Err(std::io::Error::from(std::io::ErrorKind::ConnectionAborted).into()));
    }) };
    thread::sleep(Duration::from_millis(200));
    assert_eq!(job.view.lock().unwrap().state, "transferring");
    assert!(!job.done.load(Ordering::Acquire));
    let mut stream=connect("127.0.0.1",core.info.port,Some(core.info.fingerprint.clone())).unwrap();
    write_json(&mut stream,&json!({"op":"cancel_transfer","token":job.cancel_token})).unwrap();
    assert_eq!(read_json(&mut stream).unwrap()["ok"],true);
    finishing.join().unwrap();
    assert_eq!(job.view.lock().unwrap().state,"cancelled");
    assert!(job.view.lock().unwrap().error.is_none());
    core.stop();
}

#[test]
fn large_capacity_native_metadata_has_no_fixed_file_size_limit_and_rejects_total_overflow() {
    for size in [41u64*1024*1024*1024,8u64*1024*1024*1024*1024+1,u64::MAX] {
        let offer:Offer=serde_json::from_value(json!({"sender":"large-file-test","streamingHash":true,"files":[{"name":"large.bin","size":size,"hash":""}]})).unwrap();
        validate_offer(&offer).unwrap();assert_eq!(offer.files[0].size,size);
        assert_eq!(checked_total_size(offer.files.iter().map(|file|file.size)).unwrap(),size);
    }
    let offer:Offer=serde_json::from_value(json!({"sender":"large-file-test","streamingHash":true,"files":[{"name":"large.bin","size":u64::MAX,"hash":""},{"name":"one.bin","size":1,"hash":""}]})).unwrap();
    assert!(validate_offer(&offer).is_err());assert!(checked_total_size(offer.files.iter().map(|file|file.size)).is_err());
    let mut invalid:Value=json!({"sender":"large-file-test","streamingHash":true,"files":[{"name":"large.bin","size":1,"hash":"invalid"}]});assert!(validate_offer(&serde_json::from_value(invalid.clone()).unwrap()).is_err());invalid["files"][0]["hash"]=json!("");invalid["files"][0]["name"]=json!("../escape");assert!(validate_offer(&serde_json::from_value(invalid).unwrap()).is_err());
}

#[test]
fn user_exit_terminates_partial_receive_and_retains_completed_files() {
    let (core, _dir) = stage8_instance("user exit receiver");
    let kept = core.config.download_dir.join("kept.txt"); fs::write(&kept,b"finished receipt").unwrap();
    let finished = Job::new("receive","previous",vec![],"completed"); finished.done.store(true,Ordering::Release);
    core.add_job(finished.clone()).unwrap();
    let data = vec![9;CHUNK*4];
    let (mut stream,id) = offer(&core,&data,None);
    assert_eq!(core.call(json!({"op":"accept","id":id}))["ok"],true);
    check_response(read_json(&mut stream).unwrap()).unwrap();read_json(&mut stream).unwrap();
    write_json(&mut stream,&json!({"offset":0})).unwrap();stream.write_all(&data[..CHUNK]).unwrap();stream.flush().unwrap();
    wait_for(&core,|s|s["transfers"][1]["bytes"].as_u64().unwrap_or(0)>=CHUNK as u64);
    let config = core.config.clone();
    assert_eq!(core.call(json!({"op":"terminate_user"}))["ok"],true);
    assert_eq!(core.call(json!({"op":"prepare_pull","files":[]}))["ok"],false);
    let history:Vec<Transfer>=serde_json::from_slice(&fs::read(config.state_dir.join("transfer-history.json")).unwrap()).unwrap();
    assert_eq!(history[0].state,"completed");assert!(history[0].error.is_none());
    assert_eq!(history[1].id,id);assert_eq!(history[1].state,"cancelled");assert_eq!(history[1].error.as_deref(),Some("已被用户终止"));
    assert!(history[1].finished_at>0);assert_eq!(history[1].speed,0);
    assert!(!config.download_dir.join("sample.bin").exists());
    assert_eq!(fs::read_dir(config.partial_dir.as_ref().unwrap()).unwrap().count(),0);
    assert_eq!(fs::read(&kept).unwrap(),b"finished receipt");
    core.stop();drop(stream);
    let restarted=Core::start(config).unwrap();
    assert_eq!(restarted.call(json!({"op":"snapshot"}))["transfers"][1]["error"],"已被用户终止");
    assert!(!restarted.config.state_dir.join("user-exit.pending").exists());restarted.stop();
}

#[test]
fn user_exit_cancels_pending_receive_and_reverse_offer_together() {
    let (core,dir)=stage8_instance("multiple user exit jobs");
    let (held,id)=offer(&core,b"pending",None);
    let source=dir.path().join("source.txt");fs::write(&source,b"original source").unwrap();
    let pull=core.call(json!({"op":"prepare_pull","files":[{"path":source,"name":"source.txt"}]}));
    assert_eq!(pull["ok"],true);
    assert_eq!(core.call(json!({"op":"terminate_user"}))["ok"],true);
    let history:Vec<Transfer>=serde_json::from_slice(&fs::read(core.config.state_dir.join("transfer-history.json")).unwrap()).unwrap();
    assert_eq!(history.len(),2);assert_eq!(history[0].id,id);
    for view in history{assert_eq!(view.state,"cancelled");assert_eq!(view.error.as_deref(),Some("已被用户终止"));}
    assert!(core.pulls.lock().unwrap().is_empty());assert_eq!(fs::read(source).unwrap(),b"original source");
    core.stop();drop(held);
}

#[test]
fn user_exit_marker_recovers_unfinished_journal_without_rewriting_finished_records() {
    let (core,_dir)=stage8_instance("exit recovery");let (held,_id)=offer(&core,b"unfinished",None);
    let config=core.config.clone();let journal=fs::read(config.state_dir.join("transfer-history.json")).unwrap();
    core.stop();drop(held);
    fs::write(config.state_dir.join("transfer-history.json"),journal).unwrap();
    fs::write(config.state_dir.join("user-exit.pending"),b"user exit\n").unwrap();
    let partials=config.partial_dir.as_ref().unwrap().clone();fs::create_dir_all(&partials).unwrap();
    fs::write(partials.join("abc-123.part"),b"partial").unwrap();
    fs::write(config.download_dir.join("keep.txt"),b"complete").unwrap();
    let recovered=Core::start(config).unwrap();let snapshot=recovered.call(json!({"op":"snapshot"}));
    assert_eq!(snapshot["transfers"][0]["state"],"cancelled");assert_eq!(snapshot["transfers"][0]["error"],"已被用户终止");
    assert!(!partials.join("abc-123.part").exists());assert_eq!(fs::read(recovered.config.download_dir.join("keep.txt")).unwrap(),b"complete");
    assert!(!recovered.config.state_dir.join("user-exit.pending").exists());recovered.stop();
}

#[test]
fn user_exit_stops_outbound_transfer_before_acceptance() {
    let (sender,dir)=stage8_instance("exit sender");let (receiver,_receipts)=stage8_instance("held receiver");
    let source=dir.path().join("outbound.txt");fs::write(&source,b"outbound original").unwrap();
    assert_eq!(sender.call(json!({"op":"send","peer":{"host":"127.0.0.1","port":receiver.info.port,"fingerprint":receiver.info.fingerprint},"verificationCode":receiver.info.verification_code,"files":[{"path":source,"name":"outbound.txt"}]}))["ok"],true);
    wait_for(&receiver,|s|s["transfers"][0]["state"]=="awaiting_accept");
    assert_eq!(sender.call(json!({"op":"terminate_user"}))["ok"],true);
    let history:Vec<Transfer>=serde_json::from_slice(&fs::read(sender.config.state_dir.join("transfer-history.json")).unwrap()).unwrap();
    assert_eq!(history[0].state,"cancelled");assert_eq!(history[0].error.as_deref(),Some("已被用户终止"));
    assert!(!receiver.config.download_dir.join("outbound.txt").exists());assert_eq!(fs::read(source).unwrap(),b"outbound original");
    sender.stop();receiver.stop();
}

fn user_exit_active_reverse_transfer(exit_sender:bool) {
    let (sender,sources)=stage8_instance("reverse exit sender");let (receiver,_receipts)=stage8_instance("reverse exit receiver");
    let source=sources.path().join("reverse.bin");File::create(&source).unwrap().set_len(256*1024*1024).unwrap();
    let offered=sender.call(json!({"op":"prepare_pull","files":[{"path":source,"name":"reverse.bin"}]}));assert_eq!(offered["ok"],true);
    assert_eq!(receiver.call(json!({"op":"pull","peer":{"host":"127.0.0.1","port":sender.info.port,"fingerprint":sender.info.fingerprint},"verificationCode":sender.info.verification_code,"token":offered["token"]}))["ok"],true);
    let incoming=wait_for(&receiver,|s|s["transfers"][0]["state"]=="awaiting_accept");receiver.call(json!({"op":"accept","id":incoming["transfers"][0]["id"]}));
    wait_for(&receiver,|s|s["transfers"][0]["bytes"].as_u64().unwrap_or(0)>=CHUNK as u64);
    let exiting=if exit_sender{&sender}else{&receiver};assert_eq!(exiting.call(json!({"op":"terminate_user"}))["ok"],true);
    let history:Vec<Transfer>=serde_json::from_slice(&fs::read(exiting.config.state_dir.join("transfer-history.json")).unwrap()).unwrap();
    assert_eq!(history[0].state,"cancelled");assert_eq!(history[0].error.as_deref(),Some("已被用户终止"));
    if !exit_sender{assert_eq!(fs::read_dir(receiver.config.partial_dir.as_ref().unwrap()).unwrap().count(),0);}
    assert!(!receiver.config.download_dir.join("reverse.bin").exists());assert_eq!(fs::metadata(&source).unwrap().len(),256*1024*1024);
    sender.stop();receiver.stop();
}
#[test] fn user_exit_reverse_sender_active(){user_exit_active_reverse_transfer(true);}
#[test] fn user_exit_reverse_receiver_active(){user_exit_active_reverse_transfer(false);}
