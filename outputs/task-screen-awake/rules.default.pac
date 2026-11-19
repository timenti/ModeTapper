var directHosts = __CUSTOM_DIRECT__;
var proxyHosts = __CUSTOM_PROXY__;

function FindProxyForURL(url, host) {
    host = host.toLowerCase();
    while (host) {
        if (directHosts[host] === 1) return "DIRECT";
        if (proxyHosts[host] === 1) return "PROXY 127.0.0.1:10809; SOCKS5 127.0.0.1:10808";
        var dot = host.indexOf(".");
        if (dot < 0) break;
        host = host.slice(dot + 1);
    }
    return "DIRECT";
}
