//! Runtime transport modules. Disabled LAN never advertises or admits LAN sockets.
use super::*;

pub(super) struct TransportPolicy {
    pub lan_enabled: bool,
    pub paused: AtomicBool,
    direct_enabled: bool,
    address: Mutex<Option<Ipv4Addr>>,
}
impl TransportPolicy {
    pub fn new(config: &Config) -> Result<Self> {
        let modules = config.transport_modules.clone().unwrap_or_else(|| {
            vec![if config.nearby_only { "nearby-direct" } else { "lan" }.to_string()]
        });
        ensure!(modules.iter().all(|m| m == "lan" || m == "nearby-direct"), "Unknown transport module");
        Ok(Self { paused:AtomicBool::new(false), lan_enabled: modules.iter().any(|m| m == "lan"),
            direct_enabled: modules.iter().any(|m| m == "nearby-direct"), address: Mutex::new(None) })
    }
    pub fn bind(&self, address: Option<Ipv4Addr>) -> Result<()> {
        ensure!(self.direct_enabled, "Nearby-direct module is not loaded");
        if let Some(ip) = address {
            ensure!(if_addrs::get_if_addrs()?.iter().any(|i| i.ip() == IpAddr::V4(ip)), "Direct address is not assigned to a local interface");
        }
        *self.address.lock().unwrap() = address;
        Ok(())
    }
    pub fn source(&self) -> Result<Option<Ipv4Addr>> {
        ensure!(!self.paused.load(Ordering::Acquire), "Native transport paused for browser sharing");
        let address = *self.address.lock().unwrap();
        ensure!(self.lan_enabled || address.is_some(), "Direct Wi-Fi is not ready; existing LAN transport is disabled");
        Ok(address)
    }
    pub fn addresses(&self) -> Vec<String> {
        if self.lan_enabled { local_addresses() }
        else { self.address.lock().unwrap().iter().map(ToString::to_string).collect() }
    }
    pub fn admit(&self, local: IpAddr) -> Result<()> {
        ensure!(!self.paused.load(Ordering::Acquire), "Native transport paused for browser sharing");
        if !self.lan_enabled {
            ensure!(self.source()?.map(IpAddr::V4) == Some(local), "Existing LAN transport is disabled");
        }
        Ok(())
    }
}
