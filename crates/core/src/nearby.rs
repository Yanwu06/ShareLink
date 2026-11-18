//! Ephemeral nearby control pairing. BLE carries these opaque messages; it is
//! never trusted to identify a device or carry plaintext network credentials.
use super::*;
use ring::{
    aead, agreement, hkdf,
    rand::{SecureRandom, SystemRandom},
};

const MAX_PAIRS: usize = 16;
const CONTROL_LIMIT: usize = 64 * 1024;

#[derive(Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Hello {
    version: u8,
    session_id: String,
    public_key: String,
    fingerprint: String,
    name: String,
}

struct Keys {
    send: aead::LessSafeKey,
    receive: aead::LessSafeKey,
    binding: String,
    sas: String,
    remote: Hello,
    send_seq: u64,
    receive_seq: u64,
    confirmed: bool,
}
struct Pair {
    hello: Hello,
    private: Option<agreement::EphemeralPrivateKey>,
    keys: Option<Keys>,
    expires: Instant,
}
#[derive(Default)]
pub(super) struct Pairings {
    pairs: HashMap<String, Pair>,
}

struct KeyLength(usize);
impl hkdf::KeyType for KeyLength {
    fn len(&self) -> usize {
        self.0
    }
}
fn expand(prk: &hkdf::Prk, label: &[u8], length: usize) -> Result<Vec<u8>> {
    let mut out = vec![0; length];
    prk.expand(&[label], KeyLength(length))
        .map_err(|_| anyhow!("Key derivation failed"))?
        .fill(&mut out)
        .map_err(|_| anyhow!("Key derivation failed"))?;
    Ok(out)
}
fn key(bytes: &[u8]) -> Result<aead::LessSafeKey> {
    Ok(aead::LessSafeKey::new(
        aead::UnboundKey::new(&aead::CHACHA20_POLY1305, bytes)
            .map_err(|_| anyhow!("Invalid control key"))?,
    ))
}
fn nonce(sequence: u64) -> aead::Nonce {
    let mut bytes = [0; 12];
    bytes[4..].copy_from_slice(&sequence.to_be_bytes());
    aead::Nonce::assume_unique_for_key(bytes)
}
fn aad(binding: &str, sequence: u64) -> Vec<u8> {
    let mut bytes = b"sharelink-control-v1".to_vec();
    bytes.extend_from_slice(binding.as_bytes());
    bytes.extend_from_slice(&sequence.to_be_bytes());
    bytes
}
fn validate_hello(hello: &Hello) -> Result<()> {
    ensure!(hello.version == 1, "Unsupported pairing version");
    ensure!(
        uuid::Uuid::parse_str(&hello.session_id).is_ok(),
        "Invalid pairing session"
    );
    ensure!(
        hello.public_key.len() == 64 && hex::decode(&hello.public_key)?.len() == 32,
        "Invalid pairing public key"
    );
    ensure!(
        valid_hash(&hello.fingerprint),
        "Invalid pairing fingerprint"
    );
    ensure!(
        !hello.name.is_empty()
            && hello.name.len() <= 128
            && !hello.name.chars().any(char::is_control),
        "Invalid pairing name"
    );
    Ok(())
}
impl Pairings {
    #[cfg(test)]
    pub(super) fn expire_all_for_test(&mut self) {
        for pair in self.pairs.values_mut() {
            pair.expires = Instant::now();
        }
    }
    pub(super) fn call(&mut self, r: &Value, info: &Info) -> Result<Value> {
        self.pairs.retain(|_, pair| Instant::now() < pair.expires);
        let op = r["op"].as_str().unwrap_or("");
        if op == "pair_begin" {
            ensure!(
                self.pairs.len() < MAX_PAIRS,
                "Too many nearby pairing sessions"
            );
            let random = SystemRandom::new();
            let private = agreement::EphemeralPrivateKey::generate(&agreement::X25519, &random)
                .map_err(|_| anyhow!("Cannot generate pairing key"))?;
            let public = private
                .compute_public_key()
                .map_err(|_| anyhow!("Cannot generate public key"))?;
            let pair_id = uuid::Uuid::new_v4().to_string();
            let hello = Hello {
                version: 1,
                session_id: uuid::Uuid::new_v4().to_string(),
                public_key: hex::encode(public.as_ref()),
                fingerprint: info.fingerprint.clone(),
                name: info.name.clone(),
            };
            self.pairs.insert(
                pair_id.clone(),
                Pair {
                    hello: hello.clone(),
                    private: Some(private),
                    keys: None,
                    expires: Instant::now() + Duration::from_secs(120),
                },
            );
            return Ok(json!({"ok":true,"pairId":pair_id,"hello":hello}));
        }
        let pair_id = field(r, "pairId")?;
        if op == "pair_close" {
            self.pairs.remove(&pair_id);
            return Ok(json!({"ok":true}));
        }
        let pair = self
            .pairs
            .get_mut(&pair_id)
            .ok_or_else(|| anyhow!("Pairing session expired or missing"))?;
        match op {
            "pair_finish" => {
                ensure!(pair.keys.is_none(), "Pairing already negotiated");
                let remote: Hello = serde_json::from_value(r["hello"].clone())?;
                validate_hello(&remote)?;
                ensure!(
                    remote.public_key != pair.hello.public_key
                        && remote.session_id != pair.hello.session_id,
                    "Reflected pairing hello"
                );
                let local_bytes = serde_json::to_vec(&pair.hello)?;
                let remote_bytes = serde_json::to_vec(&remote)?;
                let local_first = local_bytes < remote_bytes;
                let (first, second) = if local_first {
                    (&local_bytes, &remote_bytes)
                } else {
                    (&remote_bytes, &local_bytes)
                };
                let mut transcript = b"sharelink-nearby-pairing-v1".to_vec();
                for bytes in [first, second] {
                    transcript.extend_from_slice(&(bytes.len() as u32).to_be_bytes());
                    transcript.extend_from_slice(bytes);
                }
                let digest = Sha256::digest(&transcript);
                let public = hex::decode(&remote.public_key)?;
                let private = pair
                    .private
                    .take()
                    .ok_or_else(|| anyhow!("Pairing cannot be retried; start again"))?;
                let prk = agreement::agree_ephemeral(
                    private,
                    &agreement::UnparsedPublicKey::new(&agreement::X25519, public),
                    |secret| hkdf::Salt::new(hkdf::HKDF_SHA256, &digest).extract(secret),
                )
                .map_err(|_| anyhow!("Pairing key agreement failed"))?;
                let sas_bytes = expand(&prk, b"sharelink-sas-v1", 4)?;
                let sas = format!(
                    "{:06}",
                    u32::from_be_bytes(sas_bytes.try_into().unwrap()) % 1_000_000
                );
                let a_to_b = expand(&prk, b"sharelink-a-to-b-v1", 32)?;
                let b_to_a = expand(&prk, b"sharelink-b-to-a-v1", 32)?;
                let (send, receive) = if local_first {
                    (key(&a_to_b)?, key(&b_to_a)?)
                } else {
                    (key(&b_to_a)?, key(&a_to_b)?)
                };
                let binding = hex::encode(digest);
                pair.keys = Some(Keys {
                    send,
                    receive,
                    binding: binding.clone(),
                    sas: sas.clone(),
                    remote: remote.clone(),
                    send_seq: 0,
                    receive_seq: 0,
                    confirmed: false,
                });
                Ok(
                    json!({"ok":true,"sas":sas,"binding":binding,"peer":{"name":remote.name,"fingerprint":remote.fingerprint}}),
                )
            }
            "pair_confirm" => {
                if r["confirmed"] != true {
                    self.pairs.remove(&pair_id);
                    return Ok(json!({"ok":true,"closed":true}));
                }
                let keys = pair
                    .keys
                    .as_mut()
                    .ok_or_else(|| anyhow!("Finish pairing first"))?;
                keys.confirmed = true;
                pair.expires = Instant::now() + Duration::from_secs(1800);
                Ok(
                    json!({"ok":true,"sas":keys.sas,"peer":{"name":keys.remote.name,"fingerprint":keys.remote.fingerprint}}),
                )
            }
            "pair_encrypt" => {
                let keys = pair
                    .keys
                    .as_mut()
                    .ok_or_else(|| anyhow!("Finish pairing first"))?;
                ensure!(
                    keys.confirmed,
                    "Confirm matching codes on both devices first"
                );
                ensure!(
                    r["payload"].is_object(),
                    "Control payload must be an object"
                );
                let mut bytes = serde_json::to_vec(&r["payload"])?;
                ensure!(bytes.len() <= CONTROL_LIMIT, "Control payload too large");
                let seq = keys.send_seq;
                let next = seq
                    .checked_add(1)
                    .ok_or_else(|| anyhow!("Control sequence exhausted"))?;
                keys.send
                    .seal_in_place_append_tag(
                        nonce(seq),
                        aead::Aad::from(aad(&keys.binding, seq)),
                        &mut bytes,
                    )
                    .map_err(|_| anyhow!("Cannot encrypt control payload"))?;
                keys.send_seq = next;
                pair.expires = Instant::now() + Duration::from_secs(1800);
                Ok(
                    json!({"ok":true,"envelope":{"version":1,"binding":keys.binding,"seq":seq,"ciphertext":hex::encode(bytes)}}),
                )
            }
            "pair_decrypt" => {
                let keys = pair
                    .keys
                    .as_mut()
                    .ok_or_else(|| anyhow!("Finish pairing first"))?;
                ensure!(
                    keys.confirmed,
                    "Confirm matching codes on both devices first"
                );
                let envelope = &r["envelope"];
                ensure!(
                    envelope["version"] == 1 && envelope["binding"].as_str() == Some(&keys.binding),
                    "Wrong control session"
                );
                let seq = envelope["seq"]
                    .as_u64()
                    .ok_or_else(|| anyhow!("Missing control sequence"))?;
                ensure!(
                    seq == keys.receive_seq,
                    "Replayed or out-of-order control message"
                );
                let encoded = field(envelope, "ciphertext")?;
                ensure!(
                    encoded.len() <= (CONTROL_LIMIT + 16) * 2,
                    "Control payload too large"
                );
                let mut bytes = hex::decode(encoded)?;
                let clear = keys
                    .receive
                    .open_in_place(
                        nonce(seq),
                        aead::Aad::from(aad(&keys.binding, seq)),
                        &mut bytes,
                    )
                    .map_err(|_| anyhow!("Control authentication failed"))?;
                let payload: Value = serde_json::from_slice(clear)?;
                ensure!(payload.is_object(), "Control payload must be an object");
                keys.receive_seq = seq
                    .checked_add(1)
                    .ok_or_else(|| anyhow!("Control sequence exhausted"))?;
                pair.expires = Instant::now() + Duration::from_secs(1800);
                Ok(json!({"ok":true,"payload":payload}))
            }
            _ => bail!("Unknown pairing command"),
        }
    }
}

pub(super) fn random_token() -> Result<String> {
    let mut bytes = [0; 32];
    SystemRandom::new()
        .fill(&mut bytes)
        .map_err(|_| anyhow!("Cannot generate pull token"))?;
    Ok(hex::encode(bytes))
}
