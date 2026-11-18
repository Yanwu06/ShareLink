use std::io::{self, BufRead, Write};

fn main() -> anyhow::Result<()> {
    let path = std::env::args()
        .nth(1)
        .ok_or_else(|| anyhow::anyhow!("usage: sharelink-cli CONFIG.json"))?;
    let core = sharelink_core::Core::start(serde_json::from_slice(&std::fs::read(path)?)?)?;
    for line in io::stdin().lock().lines() {
        let result = match serde_json::from_str(&line?) {
            Ok(request) => core.call(request),
            Err(error) => serde_json::json!({"ok":false,"error":error.to_string()}),
        };
        println!("{result}");
        io::stdout().flush()?;
    }
    core.stop();
    Ok(())
}
