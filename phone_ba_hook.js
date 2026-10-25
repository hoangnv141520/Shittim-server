/**
 * Shittim Server - Frida Runtime Hook for Blue Archive Android
 * Target: Complete Local Traffic Redirection to Termux Shittim Server (127.0.0.1)
 */

console.log("[SHITTIM_FRIDA] =========================================");
console.log("[SHITTIM_FRIDA] SCRIPT LOADED AND EXECUTING!");
console.log("[SHITTIM_FRIDA] =========================================");

var LOG_PATH = "/storage/emulated/0/Android/data/com.nexon.bluearchive/files/shittim_hook.log";
var pLog = Module.findExportByName(null, "__android_log_print") || Module.findExportByName("liblog.so", "__android_log_print");
var logPrint = null;
var tagPtr = Memory.allocUtf8String("Shittim");
var fmtPtr = Memory.allocUtf8String("%s");

if (pLog) {
    try {
        logPrint = new NativeFunction(pLog, "int", ["int", "pointer", "pointer", "pointer"]);
    } catch (e) {}
}

function slog(msg) {
    console.log("[Shittim Hook] " + msg);
    try {
        var f = new File(LOG_PATH, "a");
        f.write("[Shittim Hook] " + msg + "\n");
        f.flush();
        f.close();
    } catch (e) {}

    if (logPrint) {
        try {
            var pMsg = Memory.allocUtf8String("[Shittim Hook] " + msg);
            logPrint(4, tagPtr, fmtPtr, pMsg);
        } catch (e) {}
    }
}

slog("=================================================");
slog("Shittim Frida Hook Initializing for Blue Archive...");
slog("=================================================");

var SERVER_IP = "127.0.0.1";
var API_PORT = "5000";
var GATEWAY_PORT = "5100";

function rewriteUrl(url) {
    if (!url) return url;
    var u = url.toLowerCase();

    // 1. Server config from CloudFront
    if (u.indexOf("server_config") !== -1 || (u.indexOf("cloudfront.net") !== -1 && u.indexOf(".json") !== -1)) {
        var idx = url.indexOf("/com.nexon.bluearchive");
        var path = "";
        if (idx !== -1) {
            path = url.substring(idx);
        } else {
            var netIdx = url.indexOf(".net");
            path = netIdx !== -1 ? url.substring(netIdx + 4) : url;
        }
        var newUrl = "http://" + SERVER_IP + ":" + API_PORT + path;
        slog("[Rewrite CloudFront ServerConfig] " + url + " -> " + newUrl);
        return newUrl;
    }

    // 2. Gateway rewrite (port 5100 or nxm-*-bagl gateway)
    if (u.indexOf(":5100") !== -1 || (u.indexOf("bagl.nexon.com") !== -1 && u.indexOf("/api/gateway") !== -1)) {
        var apiIdx = url.indexOf("/api");
        var path = apiIdx !== -1 ? url.substring(apiIdx) : "/api/gateway";
        var newUrl = "http://" + SERVER_IP + ":" + GATEWAY_PORT + path;
        slog("[Rewrite Gateway] " + url + " -> " + newUrl);
        return newUrl;
    }

    // 3. API rewrite (port 5000 or other bagl / nxm calls)
    if (u.indexOf("bagl.nexon.com") !== -1) {
        var apiIdx = url.indexOf("/api");
        var path = apiIdx !== -1 ? url.substring(apiIdx) : "/";
        var newUrl = "http://" + SERVER_IP + ":" + API_PORT + path;
        slog("[Rewrite API] " + url + " -> " + newUrl);
        return newUrl;
    }

    // 4. ToySDK / Inface / Nexon auth config
    if (u.indexOf("public.api.nexon.com") !== -1 ||
        u.indexOf("gtable.inface.nexon.com") !== -1 ||
        u.indexOf("config.na.nexon.com") !== -1 ||
        u.indexOf("signin.nexon.com") !== -1 ||
        u.indexOf("platform.gamescale.nexon.com") !== -1 ||
        u.indexOf("toy.log.nexon.io") !== -1 ||
        u.indexOf("toy/sdk") !== -1) {
        var schemeEnd = url.indexOf("://");
        var path = "/";
        if (schemeEnd !== -1) {
            var pathStart = url.indexOf("/", schemeEnd + 3);
            if (pathStart !== -1) path = url.substring(pathStart);
        }
        var newUrl = "http://" + SERVER_IP + ":" + API_PORT + path;
        slog("[Rewrite Nexon SDK] " + url + " -> " + newUrl);
        return newUrl;
    }

    return url;
}

// ==========================================
// 1. Native libc Socket & DNS Interception
// ==========================================
var pGetaddrinfo = Module.findExportByName("libc.so", "getaddrinfo");
if (pGetaddrinfo) {
    Interceptor.attach(pGetaddrinfo, {
        onEnter: function (args) {
            var nodePtr = args[0];
            if (!nodePtr.isNull()) {
                var node = nodePtr.readUtf8String();
                if (node) {
                    var lower = node.toLowerCase();
                    if (lower.indexOf("bagl.nexon.com") !== -1 ||
                        lower.indexOf("public.api.nexon.com") !== -1 ||
                        lower.indexOf("gtable.inface.nexon.com") !== -1 ||
                        lower.indexOf("config.na.nexon.com") !== -1 ||
                        lower.indexOf("signin.nexon.com") !== -1 ||
                        lower.indexOf("platform.gamescale.nexon.com") !== -1 ||
                        lower.indexOf("toy.log.nexon.io") !== -1) {
                        slog("[libc getaddrinfo] Redirecting " + node + " -> 127.0.0.1");
                        args[0] = Memory.allocUtf8String("127.0.0.1");
                    }
                }
            }
        }
    });
    slog("[libc] Hooked getaddrinfo");
}

var pConnect = Module.findExportByName("libc.so", "connect");
if (pConnect) {
    Interceptor.attach(pConnect, {
        onEnter: function (args) {
            var addr = args[1];
            if (!addr.isNull()) {
                var family = Memory.readU16(addr);
                if (family === 2) { // AF_INET
                    var portBe = Memory.readU16(addr.add(2));
                    var port = ((portBe & 0xFF) << 8) | ((portBe >> 8) & 0xFF);
                    if (port === 5100 || port === 5000) {
                        Memory.writeU32(addr.add(4), 0x0100007f);
                        slog("[libc connect] Port " + port + " forced to 127.0.0.1");
                    }
                }
            }
        }
    });
    slog("[libc] Hooked connect");
}

// ==========================================
// 2. SSL Pinning Bypasses
// ==========================================
var sslVerify = Module.findExportByName(null, "SSL_set_custom_verify");
if (sslVerify) {
    Interceptor.attach(sslVerify, {
        onEnter: function (args) {
            args[1] = ptr(0);
        }
    });
    slog("SSL_set_custom_verify bypassed");
}

var sslCtxVerify = Module.findExportByName(null, "SSL_CTX_set_custom_verify");
if (sslCtxVerify) {
    Interceptor.attach(sslCtxVerify, {
        onEnter: function (args) {
            args[1] = ptr(0);
        }
    });
    slog("SSL_CTX_set_custom_verify bypassed");
}

// ==========================================
// 3. Java Layer Hooks (ToySDK / Android HTTP)
// ==========================================
Java.perform(function () {
    try {
        var NetworkSecurityPolicy = Java.use('android.security.NetworkSecurityPolicy');
        NetworkSecurityPolicy.getInstance().isCleartextTrafficPermitted.overload().implementation = function () {
            return true;
        };
        NetworkSecurityPolicy.getInstance().isCleartextTrafficPermitted.overload('java.lang.String').implementation = function (hostname) {
            return true;
        };
        slog("[Java] NetworkSecurityPolicy cleartext permitted");
    } catch (e) {}

    try {
        var TrustManager = Java.use('javax.net.ssl.X509TrustManager');
        var SSLContext = Java.use('javax.net.ssl.SSLContext');

        var TrustAll = Java.registerClass({
            name: 'com.shittim.TrustAll',
            implements: [TrustManager],
            methods: {
                checkClientTrusted: function (chain, authType) {},
                checkServerTrusted: function (chain, authType) {},
                getAcceptedIssuers: function () { return []; }
            }
        });

        var trustManagers = [TrustAll.$new()];
        var SSLContext_init = SSLContext.init.overload(
            '[Ljavax.net.ssl.KeyManager;',
            '[Ljavax.net.ssl.TrustManager;',
            'java.security.SecureRandom'
        );

        SSLContext_init.implementation = function (km, tm, sr) {
            SSLContext_init.call(this, km, trustManagers, sr);
        };
        slog("[Java] SSLContext.init TrustAll hooked");
    } catch (e) {}

    try {
        var CertificatePinner = Java.use('okhttp3.CertificatePinner');
        CertificatePinner.check.overload('java.lang.String', 'java.util.List').implementation = function (hostname, peerCertificates) {
            return;
        };
        slog("[Java] OkHttp CertificatePinner bypassed");
    } catch (e) {}

    try {
        var RequestBuilder = Java.use('okhttp3.Request$Builder');
        var HttpUrl = Java.use('okhttp3.HttpUrl');

        RequestBuilder.url.overload('java.lang.String').implementation = function (urlStr) {
            var rewritten = rewriteUrl(urlStr);
            return this.url(rewritten);
        };

        RequestBuilder.url.overload('okhttp3.HttpUrl').implementation = function (httpUrl) {
            var urlStr = httpUrl.toString();
            var rewritten = rewriteUrl(urlStr);
            if (rewritten !== urlStr) {
                var newHttpUrl = HttpUrl.parse(rewritten);
                if (newHttpUrl) return this.url(newHttpUrl);
            }
            return this.url(httpUrl);
        };
        slog("[Java] OkHttp3 RequestBuilder.url hooked");
    } catch (e) {}

    try {
        var URL = Java.use('java.net.URL');
        URL.$init.overload('java.lang.String').implementation = function (spec) {
            var rewritten = rewriteUrl(spec);
            return this.$init(rewritten);
        };
        slog("[Java] java.net.URL hooked");
    } catch (e) {}
});

// ==========================================
// 4. IL2CPP Layer Hooks (UnityWebRequest & C# Uri)
// ==========================================
var il2cppHooksInstalled = false;

function installIl2cppHooks() {
    if (il2cppHooksInstalled) return true;
    var il2cpp = Process.findModuleByName("libil2cpp.so");
    if (!il2cpp) {
        slog("[IL2CPP] libil2cpp.so not loaded yet");
        return false;
    }

    var pDomainGet = Module.findExportByName("libil2cpp.so", "il2cpp_domain_get");
    if (!pDomainGet) return false;

    var il2cpp_domain_get = new NativeFunction(pDomainGet, "pointer", []);
    var domain = il2cpp_domain_get();
    if (domain.isNull()) {
        slog("[IL2CPP] Domain is null");
        return false;
    }

    var il2cpp_domain_get_assemblies = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_domain_get_assemblies"), "pointer", ["pointer", "pointer"]);
    var il2cpp_assembly_get_image = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_assembly_get_image"), "pointer", ["pointer"]);
    var il2cpp_image_get_name = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_image_get_name"), "pointer", ["pointer"]);
    var il2cpp_class_from_name = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_class_from_name"), "pointer", ["pointer", "pointer", "pointer"]);
    var il2cpp_class_get_methods = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_class_get_methods"), "pointer", ["pointer", "pointer"]);
    var il2cpp_method_get_name = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_method_get_name"), "pointer", ["pointer"]);
    var il2cpp_method_get_param_count = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_method_get_param_count"), "uint32", ["pointer"]);
    var il2cpp_string_new = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_string_new"), "pointer", ["pointer"]);
    var il2cpp_string_chars = new NativeFunction(Module.findExportByName("libil2cpp.so", "il2cpp_string_chars"), "pointer", ["pointer"]);

    var sizePtr = Memory.alloc(Process.pointerSize);
    var assemblies = il2cpp_domain_get_assemblies(domain, sizePtr);
    var size = sizePtr.readPointer().toInt32();
    slog("[IL2CPP] Scanning " + size + " assemblies for targets...");

    var hookedCount = 0;

    for (var i = 0; i < size; i++) {
        var assembly = assemblies.add(i * Process.pointerSize).readPointer();
        var image = il2cpp_assembly_get_image(assembly);
        if (image.isNull()) continue;

        // 1. Hook UnityWebRequest in UnityEngine.Networking
        var uwrClass = il2cpp_class_from_name(image, Memory.allocUtf8String("UnityEngine.Networking"), Memory.allocUtf8String("UnityWebRequest"));
        if (!uwrClass.isNull()) {
            var iter = Memory.alloc(Process.pointerSize);
            iter.writePointer(ptr(0));
            var mPtr = null;
            while (!(mPtr = il2cpp_class_get_methods(uwrClass, iter)).isNull()) {
                var mName = il2cpp_method_get_name(mPtr).readUtf8String();
                var pCount = il2cpp_method_get_param_count(mPtr);
                var fnPtr = mPtr.readPointer();
                if (fnPtr.isNull()) continue;

                if (mName === "set_url" && pCount === 1) {
                    try {
                        Interceptor.attach(fnPtr, {
                            onEnter: function (args) {
                                var strObj = args[1];
                                if (!strObj.isNull()) {
                                    var origUrl = il2cpp_string_chars(strObj).readUtf16String();
                                    var newUrl = rewriteUrl(origUrl);
                                    if (newUrl !== origUrl) {
                                        args[1] = il2cpp_string_new(Memory.allocUtf8String(newUrl));
                                    }
                                }
                            }
                        });
                        hookedCount++;
                        slog("[IL2CPP] Hooked UnityWebRequest.set_url");
                    } catch (e) {}
                } else if (mName === "Get" && pCount === 1) {
                    try {
                        Interceptor.attach(fnPtr, {
                            onEnter: function (args) {
                                var strObj = args[0];
                                if (!strObj.isNull()) {
                                    var origUrl = il2cpp_string_chars(strObj).readUtf16String();
                                    var newUrl = rewriteUrl(origUrl);
                                    if (newUrl !== origUrl) {
                                        args[0] = il2cpp_string_new(Memory.allocUtf8String(newUrl));
                                    }
                                }
                            }
                        });
                        hookedCount++;
                        slog("[IL2CPP] Hooked UnityWebRequest.Get");
                    } catch (e) {}
                }
            }
        }

        // 2. Hook System.Uri constructors
        var uriClass = il2cpp_class_from_name(image, Memory.allocUtf8String("System"), Memory.allocUtf8String("Uri"));
        if (!uriClass.isNull()) {
            var iterUri = Memory.alloc(Process.pointerSize);
            iterUri.writePointer(ptr(0));
            var mUriPtr = null;
            while (!(mUriPtr = il2cpp_class_get_methods(uriClass, iterUri)).isNull()) {
                var mName = il2cpp_method_get_name(mUriPtr).readUtf8String();
                var fnPtr = mUriPtr.readPointer();
                if (fnPtr.isNull()) continue;

                if (mName === ".ctor") {
                    try {
                        Interceptor.attach(fnPtr, {
                            onEnter: function (args) {
                                // Check args[1]
                                var strObj = args[1];
                                if (!strObj.isNull()) {
                                    try {
                                        var chars = il2cpp_string_chars(strObj);
                                        var origUrl = chars.readUtf16String();
                                        if (origUrl && (origUrl.indexOf("http://") === 0 || origUrl.indexOf("https://") === 0)) {
                                            var newUrl = rewriteUrl(origUrl);
                                            if (newUrl !== origUrl) {
                                                args[1] = il2cpp_string_new(Memory.allocUtf8String(newUrl));
                                            }
                                        }
                                    } catch (err) {}
                                }
                                // Check args[2]
                                var strObj2 = args[2];
                                if (!strObj2.isNull()) {
                                    try {
                                        var chars2 = il2cpp_string_chars(strObj2);
                                        var origUrl2 = chars2.readUtf16String();
                                        if (origUrl2 && (origUrl2.indexOf("http://") === 0 || origUrl2.indexOf("https://") === 0)) {
                                            var newUrl2 = rewriteUrl(origUrl2);
                                            if (newUrl2 !== origUrl2) {
                                                args[2] = il2cpp_string_new(Memory.allocUtf8String(newUrl2));
                                            }
                                        }
                                    } catch (err) {}
                                }
                            }
                        });
                        hookedCount++;
                    } catch (e) {}
                }
            }
            slog("[IL2CPP] Hooked System.Uri constructors");
        }
    }

    il2cppHooksInstalled = true;
    slog("[IL2CPP] Installation finished. Total targets hooked: " + hookedCount);
    return true;
}

function hookIl2cppInit() {
    var pInit = Module.findExportByName("libil2cpp.so", "il2cpp_init");
    if (pInit) {
        Interceptor.attach(pInit, {
            onLeave: function (retval) {
                slog("[IL2CPP] il2cpp_init returned, executing installIl2cppHooks...");
                installIl2cppHooks();
            }
        });
        slog("[IL2CPP] Attached to il2cpp_init");
        return true;
    }
    return false;
}

// Check if libil2cpp is already loaded
if (Process.findModuleByName("libil2cpp.so")) {
    slog("libil2cpp.so already in memory!");
    if (!installIl2cppHooks()) {
        hookIl2cppInit();
    }
} else {
    // Intercept library loading
    var pDlopenExt = Module.findExportByName(null, "android_dlopen_ext");
    if (pDlopenExt) {
        Interceptor.attach(pDlopenExt, {
            onEnter: function (args) {
                if (!args[0].isNull()) {
                    this.name = args[0].readCString();
                }
            },
            onLeave: function (retval) {
                if (this.name && this.name.indexOf("libil2cpp.so") !== -1) {
                    slog("android_dlopen_ext loaded libil2cpp.so!");
                    hookIl2cppInit();
                }
            }
        });
        slog("Attached to android_dlopen_ext");
    }

    var pDlopen = Module.findExportByName(null, "dlopen");
    if (pDlopen) {
        Interceptor.attach(pDlopen, {
            onEnter: function (args) {
                if (!args[0].isNull()) {
                    this.name = args[0].readCString();
                }
            },
            onLeave: function (retval) {
                if (this.name && this.name.indexOf("libil2cpp.so") !== -1) {
                    slog("dlopen loaded libil2cpp.so!");
                    hookIl2cppInit();
                }
            }
        });
        slog("Attached to dlopen");
    }
}

slog("Shittim Frida Script Armed and Ready!");
