//! Read-only AP identification. No address prefixes or interface names identify a role.
use anyhow::Result;
use std::net::Ipv4Addr;

#[cfg(any(target_os = "linux", target_env = "ohos", test))]
#[derive(Clone)]
struct Candidate { address: Ipv4Addr, up: bool, mode: Option<u32> }

#[cfg(any(target_os = "linux", target_env = "ohos", test))]
fn select_address(candidates: Vec<Candidate>) -> Result<Ipv4Addr> {
    let mut addresses: Vec<_> = candidates.into_iter()
        .filter(|i| i.up && i.mode == Some(3) && i.address.is_private())
        .map(|i| i.address).collect();
    addresses.sort(); addresses.dedup();
    anyhow::ensure!(addresses.len() == 1, "Cannot uniquely identify a personal hotspot AP address");
    Ok(addresses[0])
}

// This state comes from the local Harmony UI's public Wi-Fi APIs, never a remote peer.
#[cfg(any(target_env = "ohos", test))]
#[derive(serde::Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HotspotContext { active: bool, station_connected: bool, station_address: Option<Ipv4Addr>, p2p_connected: bool }

#[cfg(any(target_env = "ohos", test))]
struct WirelessInterface { addresses: Vec<Ipv4Addr>, wireless: bool, bridged: bool }

#[cfg(any(target_env = "ohos", test))]
fn select_from_context(context: &HotspotContext, interfaces: Vec<WirelessInterface>) -> Result<Ipv4Addr> {
    anyhow::ensure!(context.active && !context.p2p_connected, "Personal hotspot state is inactive or overlaps P2P");
    anyhow::ensure!(!interfaces.iter().any(|i| i.bridged), "Bridged hotspot topology is not supported");
    // The public AP_STARTED state follows AP IPv4/DHCP setup. Addressless links
    // cannot own this IPv4 listener; inactive P2P control links can remain UP.
    // Never discard an extra IPv4-bearing wireless link or an address alias.
    let wireless: Vec<_> = interfaces.into_iter().filter(|i| i.wireless && !i.addresses.is_empty()).collect();
    anyhow::ensure!(wireless.iter().all(|i| i.addresses.len() == 1), "Wireless interface IPv4 addresses are ambiguous");
    let expected = if context.station_connected { 2 } else { 1 };
    anyhow::ensure!(wireless.len() == expected, "Wireless interface roles are not unique");
    let station = if context.station_connected {
        let ip = context.station_address.ok_or_else(|| anyhow::anyhow!("Missing public station address"))?;
        anyhow::ensure!(!ip.is_unspecified() && !ip.is_loopback() && !ip.is_multicast() && !ip.is_broadcast(), "Invalid public station address");
        anyhow::ensure!(wireless.iter().filter(|i| i.addresses[0] == ip).count() == 1, "Public station address does not uniquely match a wireless interface");
        Some(ip)
    } else {
        anyhow::ensure!(context.station_address.is_none(), "Disconnected station has stale address");
        None
    };
    let addresses: Vec<_> = wireless.into_iter().map(|i| i.addresses[0]).filter(|ip| Some(*ip) != station).collect();
    anyhow::ensure!(addresses.len() == 1 && addresses[0].is_private(), "Cannot uniquely identify a private hotspot AP address");
    Ok(addresses[0])
}

#[cfg(any(target_os = "linux", target_env = "ohos"))]
pub(super) fn address(context: Option<&serde_json::Value>) -> Result<Ipv4Addr> {
    use std::{collections::HashMap, ffi::CStr, os::fd::AsRawFd};
    use socket2::{Domain, Socket, Type};
    #[repr(C)]
    union WirelessData { mode: u32, storage: [u64; 2] }
    #[repr(C)]
    struct WirelessRequest { name: [libc::c_char; libc::IFNAMSIZ], data: WirelessData }
    let socket = Socket::new(Domain::IPV4, Type::DGRAM, None)?;
    let mut interfaces = std::ptr::null_mut();
    if unsafe { libc::getifaddrs(&mut interfaces) } != 0 { return Err(std::io::Error::last_os_error().into()); }
    struct Interfaces(*mut libc::ifaddrs);
    impl Drop for Interfaces { fn drop(&mut self) { unsafe { libc::freeifaddrs(self.0); } } }
    let _guard = Interfaces(interfaces);
    let mut links: HashMap<String, Vec<Ipv4Addr>> = HashMap::new();
    let mut current = interfaces;
    while !current.is_null() {
        let interface = unsafe { &*current };
        if !interface.ifa_name.is_null() && interface.ifa_flags & libc::IFF_UP as u32 != 0 && interface.ifa_flags & libc::IFF_LOOPBACK as u32 == 0 {
            let name = unsafe { CStr::from_ptr(interface.ifa_name) }.to_str()?;
            anyhow::ensure!(!name.is_empty() && name.len() < libc::IFNAMSIZ && name != "." && name != ".." && !name.contains(['/', '\\']), "Invalid kernel interface name");
            let addresses = links.entry(name.to_owned()).or_default();
            if !interface.ifa_addr.is_null() && unsafe { (*interface.ifa_addr).sa_family } as i32 == libc::AF_INET {
                let ip = unsafe { &*(interface.ifa_addr as *const libc::sockaddr_in) };
                let ip = Ipv4Addr::from(u32::from_be(ip.sin_addr.s_addr));
                if !ip.is_unspecified() && !ip.is_loopback() && !ip.is_multicast() && !ip.is_broadcast() && !addresses.contains(&ip) { addresses.push(ip); }
            }
        }
        current = interface.ifa_next;
    }
    let mut candidates = Vec::new();
    for (name, addresses) in &links {
        let mut request = WirelessRequest { name: [0; libc::IFNAMSIZ], data: WirelessData { storage: [0; 2] } };
        for (dst, src) in request.name.iter_mut().zip(name.as_bytes()) { *dst = *src as libc::c_char; }
        let status = unsafe { libc::ioctl(socket.as_raw_fd(), 0x8B07 as _, &mut request) };
        let mode = if status == 0 { Some(unsafe { request.data.mode }) } else { None };
        for ip in addresses { candidates.push(Candidate { address: *ip, up: true, mode }); }
    }
    if candidates.iter().any(|i| i.mode == Some(3)) { return select_address(candidates); }
    #[cfg(target_env = "ohos")]
    {
        // cfg80211 registers phy80211/wireless for every wireless link. AP_STARTED
        // follows address/DHCP setup. With public STA matched and P2P disconnected,
        // exactly one remaining, unbridged UP wireless IPv4 link identifies the AP.
        // Any extra/missing wireless link, alias, unreadable metadata or bridge fails.
        fn present(path: &std::path::Path) -> Result<bool> {
            match std::fs::metadata(path) { Ok(_) => Ok(true), Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(false), Err(e) => Err(e.into()) }
        }
        let context: HotspotContext = serde_json::from_value(context.ok_or_else(|| anyhow::anyhow!("Missing local hotspot state"))?.clone())?;
        let mut evidence = Vec::new();
        for (name, addresses) in links {
            let path = std::path::Path::new("/sys/class/net").join(name);
            let phy = present(&path.join("phy80211"))?;
            let wireless = present(&path.join("wireless"))?;
            anyhow::ensure!(phy == wireless, "Inconsistent kernel wireless metadata");
            evidence.push(WirelessInterface { addresses, wireless: phy && wireless, bridged: present(&path.join("bridge"))? || present(&path.join("master"))? });
        }
        return select_from_context(&context, evidence);
    }
    #[cfg(not(target_env = "ohos"))]
    { let _ = context; anyhow::bail!("Cannot uniquely identify a personal hotspot AP address") }
}

#[cfg(not(any(target_os = "linux", target_env = "ohos")))]
pub(super) fn address(_context: Option<&serde_json::Value>) -> Result<Ipv4Addr> { anyhow::bail!("Personal hotspot AP inspection is not supported on this platform") }
#[cfg(test)]
mod tests {
    use super::*;
    fn candidate(ip: &str, up: bool, mode: Option<u32>) -> Candidate { Candidate { address: ip.parse().unwrap(), up, mode } }
    #[test]
    fn only_proven_ap_is_selected_among_station_and_ethernet() {
        let found = select_address(vec![candidate("192.168.1.2", true, Some(2)), candidate("10.1.1.2", true, None), candidate("192.168.43.1", true, Some(3))]).unwrap();
        assert_eq!(found.to_string(), "192.168.43.1");
    }
    #[test]
    fn no_proof_never_falls_back_to_lan_or_p2p() {
        assert!(select_address(vec![candidate("192.168.1.2", true, Some(2)), candidate("192.168.49.1", true, Some(0)), candidate("10.0.0.1", true, None)]).is_err());
    }
    #[test]
    fn ambiguous_or_inactive_ap_is_rejected() {
        assert!(select_address(vec![candidate("192.168.43.1", true, Some(3)), candidate("192.168.44.1", true, Some(3))]).is_err());
        assert!(select_address(vec![candidate("192.168.43.1", false, Some(3))]).is_err());
    }
    #[test]
    fn non_private_addresses_are_rejected_and_duplicate_ap_is_one() {
        for ip in ["127.0.0.1", "169.254.1.1", "0.0.0.0", "8.8.8.8"] { assert!(select_address(vec![candidate(ip, true, Some(3))]).is_err()); }
        assert!(select_address(vec![candidate("10.0.0.1", true, Some(3)), candidate("10.0.0.1", true, Some(3))]).is_ok());
    }

    fn context(station: Option<&str>) -> HotspotContext { HotspotContext { active:true,station_connected:station.is_some(),station_address:station.map(|i|i.parse().unwrap()),p2p_connected:false } }
    fn link(ips: &[&str], wireless:bool, bridged:bool) -> WirelessInterface { WirelessInterface { addresses:ips.iter().map(|i|i.parse().unwrap()).collect(),wireless,bridged } }
    #[test]
    fn context_matches_public_station_and_never_selects_cellular_or_lan() {
        let found=select_from_context(&context(Some("10.20.30.40")),vec![link(&["10.20.30.40"],true,false),link(&["192.168.43.1"],true,false),link(&["10.1.2.3"],false,false),link(&["172.16.3.4"],false,false)]).unwrap();
        assert_eq!(found.to_string(),"192.168.43.1");
    }
    #[test]
    fn context_supports_cellular_uplink_with_only_one_wireless_link() {
        assert_eq!(select_from_context(&context(None),vec![link(&["172.24.9.6"],true,false),link(&["10.1.2.3"],false,false)]).unwrap().to_string(),"172.24.9.6");
    }
    #[test]
    fn context_rejects_inactive_p2p_missing_and_unknown_state() {
        let mut c=context(None); c.active=false; assert!(select_from_context(&c,vec![link(&["10.0.0.1"],true,false)]).is_err());
        c.active=true;c.p2p_connected=true;assert!(select_from_context(&c,vec![link(&["10.0.0.1"],true,false)]).is_err());
        for value in [serde_json::json!({"active":true}),serde_json::json!({"active":true,"stationConnected":false,"p2pConnected":false,"unexpected":true}),serde_json::json!({"active":true,"stationConnected":false,"p2pConnected":null})] { assert!(serde_json::from_value::<HotspotContext>(value).is_err()); }
    }
    #[test]
    fn context_rejects_stale_missing_and_byte_swapped_station_address() {
        for ip in ["40.30.20.10","0.0.0.0","127.0.0.1","224.0.0.1"] { assert!(select_from_context(&context(Some(ip)),vec![link(&["10.20.30.40"],true,false),link(&["192.168.43.1"],true,false)]).is_err()); }
        let mut c=context(Some("10.20.30.40"));c.station_address=None;assert!(select_from_context(&c,vec![link(&["10.20.30.40"],true,false),link(&["192.168.43.1"],true,false)]).is_err());
        let mut c=context(None);c.station_address=Some("10.20.30.40".parse().unwrap());assert!(select_from_context(&c,vec![link(&["192.168.43.1"],true,false)]).is_err());
    }
    #[test]
    fn context_ignores_addressless_links_but_rejects_extra_ipv4_roles() {
        let c=context(Some("10.20.30.40"));
        let found=select_from_context(&c,vec![link(&["10.20.30.40"],true,false),link(&["192.168.43.1"],true,false),link(&[],true,false)]).unwrap();
        assert_eq!(found.to_string(),"192.168.43.1");
        assert!(select_from_context(&c,vec![link(&["10.20.30.40"],true,false),link(&["192.168.43.1"],true,false),link(&["192.168.49.1"],true,false)]).is_err());
        assert!(select_from_context(&context(None),vec![link(&[],true,false)]).is_err());
    }
    #[test]
    fn context_rejects_aliases_bridges_and_duplicate_station_links() {
        for links in [vec![link(&["10.0.0.1","10.0.0.2"],true,false)],vec![link(&["10.0.0.1"],true,true)],vec![link(&["10.0.0.1"],true,false),link(&["172.16.1.1"],false,true)]] { assert!(select_from_context(&context(None),links).is_err()); }
        assert!(select_from_context(&context(Some("10.0.0.1")),vec![link(&["10.0.0.1"],true,false),link(&["10.0.0.1"],true,false)]).is_err());
    }
    #[test]
    fn context_rejects_nonprivate_ap_and_missing_wireless_link() {
        for ip in ["8.8.8.8","169.254.1.1","0.0.0.0"] { assert!(select_from_context(&context(None),vec![link(&[ip],true,false)]).is_err()); }
        assert!(select_from_context(&context(None),vec![link(&["192.168.43.1"],false,false)]).is_err());
        assert!(select_from_context(&context(None),vec![]).is_err());
    }
}
