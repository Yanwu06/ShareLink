//! Bounded TLS memory probes and conservative path cost comparison.
use super::*;
const PROBE_LIMIT: u64 = 16 * 1024 * 1024;
const PROBE_BLOCK: usize = 64 * 1024;
const PROBE_DURATION: Duration = Duration::from_secs(1);

fn result(bytes: u64, elapsed: Duration, direction: &str) -> Value {
    let seconds = elapsed.as_secs_f64().max(0.000_001);
    // A byte-limited loopback sample is useful, but cannot establish sustained
    // throughput or disk speed. Never report it with high confidence.
    let confidence = if elapsed >= Duration::from_millis(250) && bytes >= 1024 * 1024 {
        "medium"
    } else {
        "low"
    };
    json!({"ok":true,"direction":direction,"bytesPerSecond":(bytes as f64 / seconds) as u64,
        "bytes":bytes,"durationMs":elapsed.as_millis().max(1),"confidence":confidence,
        "source":"tls-memory-probe","measuredAt":epoch(),"storageMeasured":false})
}
fn write_probe<W: Write>(stream: &mut W) -> Result<u64> {
    let began = Instant::now();
    let buffer = vec![0x5a; PROBE_BLOCK];
    let mut bytes = 0;
    while bytes < PROBE_LIMIT && began.elapsed() < PROBE_DURATION {
        stream.write_all(&(PROBE_BLOCK as u32).to_be_bytes())?;
        stream.write_all(&buffer)?;
        stream.flush()?;
        bytes += PROBE_BLOCK as u64;
    }
    stream.write_all(&0u32.to_be_bytes())?;
    stream.flush()?;
    Ok(bytes)
}
fn read_probe<R: Read>(stream: &mut R) -> Result<(u64, Duration)> {
    let began = Instant::now();
    let mut bytes = 0;
    let mut buffer = vec![0; PROBE_BLOCK];
    loop {
        ensure!(
            began.elapsed() < Duration::from_secs(3),
            "Measurement exceeded time budget"
        );
        let mut header = [0; 4];
        stream.read_exact(&mut header)?;
        let length = u32::from_be_bytes(header) as usize;
        if length == 0 {
            break;
        }
        ensure!(
            length <= PROBE_BLOCK && bytes + length as u64 <= PROBE_LIMIT,
            "Measurement exceeded byte budget"
        );
        stream.read_exact(&mut buffer[..length])?;
        bytes += length as u64;
    }
    Ok((bytes, began.elapsed()))
}
#[cfg(test)]
pub(super) fn test_read_probe<R: Read>(stream: &mut R) -> Result<(u64, Duration)> {
    read_probe(stream)
}
pub(super) fn serve_measure(
    stream: &mut StreamOwned<ServerConnection, TcpStream>,
    request: &Value,
) -> Result<()> {
    let direction = field(request, "direction")?;
    ensure!(
        direction == "upload" || direction == "download",
        "Unknown measurement direction"
    );
    stream.sock.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream
        .sock
        .set_write_timeout(Some(Duration::from_secs(2)))?;
    write_json(
        stream,
        &json!({"ok":true,"ready":true,"maxBytes":PROBE_LIMIT,"durationMs":1000}),
    )?;
    if direction == "upload" {
        let (bytes, elapsed) = read_probe(stream)?;
        write_json(stream, &result(bytes, elapsed, &direction))?;
    } else {
        write_probe(stream)?;
    }
    Ok(())
}
pub(super) fn measure(r: &Value, source: Option<Ipv4Addr>) -> Result<Value> {
    let peer = &r["peer"];
    let host = field(peer, "host")?;
    let port = port_field(peer)?;
    let pin = verify_peer(r)?;
    let direction = field(r, "direction")?;
    ensure!(
        direction == "upload" || direction == "download",
        "Unknown measurement direction"
    );
    let mut stream = connect_from(&host, port, Some(pin), source)?;
    stream.sock.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream
        .sock
        .set_write_timeout(Some(Duration::from_secs(2)))?;
    write_json(
        &mut stream,
        &json!({"op":"measure","version":1,"direction":direction}),
    )?;
    check_response(read_json(&mut stream)?)?;
    if direction == "upload" {
        write_probe(&mut stream)?;
        check_response(read_json(&mut stream)?)
    } else {
        let (bytes, elapsed) = read_probe(&mut stream)?;
        Ok(result(bytes, elapsed, &direction))
    }
}

fn rate(r: &Value, name: &str) -> Result<Option<f64>> {
    if r[name].is_null() {
        return Ok(None);
    }
    let n = r[name].as_f64().ok_or_else(|| anyhow!("Invalid {name}"))?;
    ensure!(
        n.is_finite() && n > 0.0,
        "{name} must be positive or null for unknown"
    );
    Ok(Some(n))
}
pub(super) fn select_path(r: &Value) -> Result<Value> {
    let bytes = r["remainingBytes"]
        .as_u64()
        .ok_or_else(|| anyhow!("Missing remainingBytes"))?;
    let current = rate(r, "currentBytesPerSecond")?;
    let candidate = rate(r, "candidateBytesPerSecond")?;
    let setup = if r["setupSeconds"].is_null() {
        None
    } else {
        let v = r["setupSeconds"]
            .as_f64()
            .ok_or_else(|| anyhow!("Invalid setupSeconds"))?;
        ensure!(v.is_finite() && v >= 0.0, "Invalid setupSeconds");
        Some(v)
    };
    let current_time = current.map(|n| bytes as f64 / n);
    let candidate_time = candidate
        .zip(setup)
        .map(|(n, setup)| setup + bytes as f64 / n);
    let savings = current_time.zip(candidate_time).map(|(a, b)| a - b);
    let (choice, reason) = if bytes == 0 {
        ("current", "nothing-remaining")
    } else if current.is_none() && candidate.is_none() {
        ("unavailable", "all-rates-unknown")
    } else if r["keepCurrentNetwork"] == true {
        ("current", "keep-current-network")
    } else if candidate.is_none() || setup.is_none() {
        ("current", "candidate-or-setup-unknown")
    } else if current.is_none() {
        ("current", "current-rate-unknown")
    } else if r["candidateAvailable"] == false {
        ("current", "candidate-unavailable")
    } else if let (Some(saved), Some(current_time)) = (savings, current_time) {
        if saved >= 3.0 && saved >= current_time * 0.20 {
            ("candidate", "conservative-benefit")
        } else {
            ("current", "benefit-below-threshold")
        }
    } else {
        ("current", "insufficient-information")
    };
    let source = |name: &str| r[name].as_str().unwrap_or("caller-estimate").to_string();
    Ok(json!({"ok":true,"choice":choice,"reason":reason,
        "currentEstimatedSeconds":current_time,"candidateEstimatedSeconds":candidate_time,
        "savedSeconds":savings,"minimumSavedSeconds":3,"minimumSavedFraction":0.2,
        "currentRateSource":source("currentRateSource"),"candidateRateSource":source("candidateRateSource"),
        "ratesAreCallerProvided":true}))
}
