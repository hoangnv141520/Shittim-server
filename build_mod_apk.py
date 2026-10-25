import os
import sys
import shutil
import zipfile
import subprocess
import struct

WORKSPACE_DIR = os.path.dirname(os.path.abspath(__file__))
BA_APK_DIR = os.path.join(WORKSPACE_DIR, "ba_apk")
OUTPUT_DIR = os.path.join(WORKSPACE_DIR, "ba_mod")
KEYSTORE_PATH = os.path.join(BA_APK_DIR, "shittim.keystore")
UBER_SIGNER_JAR = r"D:\apk\tool\uber-apk-signer.jar"
ZIPALIGN_EXE = r"D:\apk\tool\zipalign.exe"
GATEWAY_PUB_KEY = os.path.join(WORKSPACE_DIR, "Shittim-Server", "config", "GatewayPublicKey.pem")

BASE_APK = os.path.join(BA_APK_DIR, "base.apk")
SPLIT_ARM64_APK = os.path.join(BA_APK_DIR, "split_config.arm64_v8a.apk")

OFFICIAL_RSA_PEM = (
    "-----BEGIN PUBLIC KEY-----\n"
    "MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAz1oODtVINPQsTIbFDiL2\n"
    "IIj3YqDGxZ1WqJWfJrpo7ZqdnSIMXaVqVyM07Zguc9wczPrluDNowheP8NXe8uTR\n"
    "prO7jpF9bOcrrit9Xyxf57NA5rIMqOj6/XgWzzptvvt3oUrOQJRTvEjusQCX79Ds\n"
    "fhhbPRfdPzZUOPYa49MM385Z05ndNiTB9/yhxP5+5zm7TS3f6/o1Ntw9yXoRYmgR\n"
    "8+JYGL6TpqtZHkqPTp6kIJrnsLyT1wRteh3oQKJaze0CM0DUUzFiqieRE8c8JAoV\n"
    "jk/yRQUqVMoKbdpINYlWhF+GFE/ccSpqQymepHxfR0alspynqJwJgODCeHtwuno5\n"
    "HEOwdbA8NanLVnE9qlvdeNkbODdt/XTHKOaXx+SUMHezUY0rEXp8WMim0i9IQTGa\n"
    "YWeW25RjXIZswKdg0gLRJXYZXUgF3nVcqr0hi4kv1pgrFPsjI7yIThxLY0UfomkR\n"
    "oe6sSBBtQUDI85s+1pZRcl8xygxOxMfFSk5RB3EntFmcf/AKaLYQbXN9RxgrvqRS\n"
    "9koBBEnnrIN0zXDBioZUDOWVBeBZwAydpPGKu2mkqu3BI/Al92noEHc6fmkG5+Qm\n"
    "b237xSh1DFbbQO6lxg4ABdsqgvZskYS+7BcQPjs1z2zftrnesFkqD7BhbqXUBuDY\n"
    "D80B8Be5I483qSSy8DsW458CAwEAAQ==\n"
    "-----END PUBLIC KEY-----"
)

TARGETS_5000 = [
    "https://nxm-eu-bagl.nexon.com:5000/api/",
    "https://nxm-ios-bagl.nexon.com:5000/api/",
    "https://nxm-kr-bagl.nexon.com:5000/api/",
    "https://nxm-or-bagl.nexon.com:5000/api/",
    "https://nxm-stg-bagl.nexon.com:5006/api/",
    "https://nxm-th-bagl.nexon.com:5000/api/",
    "https://nxm-tw-bagl.nexon.com:5000/api/",
]

TARGETS_5100 = [
    "https://nxm-eu-bagl.nexon.com:5100/api/",
    "https://nxm-ios-bagl.nexon.com:5100/api/",
    "https://nxm-kr-bagl.nexon.com:5100/api/",
    "https://nxm-or-bagl.nexon.com:5100/api/",
    "https://nxm-stg-bagl.nexon.com:5106/api/",
    "https://nxm-th-bagl.nexon.com:5100/api/",
    "https://nxm-tw-bagl.nexon.com:5100/api/",
]

NEW_5000 = b"http://127.0.0.1:5000/api/"
NEW_5100 = b"http://127.0.0.1:5100/api/"


def patch_metadata(data_bytes: bytes) -> bytes:
    data = bytearray(data_bytes)
    print("[*] Patching global-metadata.dat...")

    # 1. Patch RSA Gateway Public Key (4096-bit split: 272, 272, 255)
    with open(GATEWAY_PUB_KEY, "r", encoding="utf-8") as f:
        shittim_pem = f.read().replace("\r\n", "\n").strip()

    off_chunks = [
        OFFICIAL_RSA_PEM[:272].encode("ascii"),
        OFFICIAL_RSA_PEM[272:544].encode("ascii"),
        OFFICIAL_RSA_PEM[544:].encode("ascii"),
    ]
    shi_chunks = [
        shittim_pem[:272].encode("ascii"),
        shittim_pem[272:544].encode("ascii"),
        shittim_pem[544:].encode("ascii"),
    ]

    for i in range(3):
        pos = data.find(off_chunks[i])
        if pos != -1:
            data[pos:pos + len(shi_chunks[i])] = shi_chunks[i]
            print(f"[+] Replaced official RSA chunk {i} at 0x{pos:X}")
        else:
            # Check if already patched
            pos_shi = data.find(shi_chunks[i])
            if pos_shi != -1:
                print(f"[+] RSA chunk {i} already patched at 0x{pos_shi:X}")
            else:
                raise RuntimeError(f"Could not find RSA chunk {i} in metadata!")

    # 2. Patch string literal endpoints
    lit_offset, lit_size, data_offset, data_size = struct.unpack("<IIII", data[8:24])
    patched_count = 0

    for i in range(lit_offset, lit_offset + lit_size, 8):
        length, at = struct.unpack("<ii", data[i:i + 8])
        if 0 <= at and at + length <= data_size:
            s = bytes(data[data_offset + at : data_offset + at + length]).decode("utf-8", errors="ignore")
            if s in TARGETS_5000:
                assert len(NEW_5000) <= length
                data[data_offset + at : data_offset + at + len(NEW_5000)] = NEW_5000
                data[i:i + 4] = struct.pack("<i", len(NEW_5000))
                patched_count += 1
                print(f"[+] Patched Gateway URL: {s} -> {NEW_5000.decode()}")
            elif s in TARGETS_5100:
                assert len(NEW_5100) <= length
                data[data_offset + at : data_offset + at + len(NEW_5100)] = NEW_5100
                data[i:i + 4] = struct.pack("<i", len(NEW_5100))
                patched_count += 1
                print(f"[+] Patched API URL: {s} -> {NEW_5100.decode()}")

    print(f"[+] Successfully patched {patched_count} endpoint literals in global-metadata.dat")
    return bytes(data)


def prepare_base_apk():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    unsigned_base = os.path.join(OUTPUT_DIR, "base_mod_unsigned.apk")
    print(f"[*] Processing {BASE_APK} -> {unsigned_base}...")

    with zipfile.ZipFile(BASE_APK, "r") as zf_in, zipfile.ZipFile(unsigned_base, "w") as zf_out:
        for item in zf_in.infolist():
            # Strip signatures only
            if item.filename.startswith("META-INF/"):
                upper = item.filename.upper()
                if (
                    upper.endswith(".SF")
                    or upper.endswith(".RSA")
                    or upper.endswith(".DSA")
                    or upper.endswith(".EC")
                    or upper.endswith("MANIFEST.MF")
                ):
                    continue

            data = zf_in.read(item.filename)
            if item.filename == "assets/bin/Data/Managed/Metadata/global-metadata.dat":
                data = patch_metadata(data)

            c_type = zipfile.ZIP_STORED if (item.filename == "resources.arsc" or item.filename.endswith(".so")) else zipfile.ZIP_DEFLATED
            new_info = zipfile.ZipInfo(item.filename, item.date_time)
            new_info.compress_type = c_type
            zf_out.writestr(new_info, data)

    print("[+] Prepared base_mod_unsigned.apk successfully")
    return unsigned_base


def prepare_split_apk():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    unsigned_split = os.path.join(OUTPUT_DIR, "split_config.arm64_v8a_mod_unsigned.apk")
    print(f"[*] Processing {SPLIT_ARM64_APK} -> {unsigned_split}...")

    with zipfile.ZipFile(SPLIT_ARM64_APK, "r") as zf_in, zipfile.ZipFile(unsigned_split, "w") as zf_out:
        for item in zf_in.infolist():
            if item.filename.startswith("META-INF/"):
                upper = item.filename.upper()
                if (
                    upper.endswith(".SF")
                    or upper.endswith(".RSA")
                    or upper.endswith(".DSA")
                    or upper.endswith(".EC")
                    or upper.endswith("MANIFEST.MF")
                ):
                    continue

            data = zf_in.read(item.filename)
            c_type = zipfile.ZIP_STORED if (item.filename == "resources.arsc" or item.filename.endswith(".so")) else zipfile.ZIP_DEFLATED
            new_info = zipfile.ZipInfo(item.filename, item.date_time)
            new_info.compress_type = c_type
            zf_out.writestr(new_info, data)

    print("[+] Prepared split_config.arm64_v8a_mod_unsigned.apk successfully")
    return unsigned_split


def align_and_sign(apks):
    signed_dir = os.path.join(OUTPUT_DIR, "aligned_signed")
    os.makedirs(signed_dir, exist_ok=True)

    aligned_apks = []
    for apk in apks:
        base_name = os.path.splitext(os.path.basename(apk))[0]
        aligned_apk = os.path.join(OUTPUT_DIR, f"{base_name}_aligned.apk")
        print(f"[*] Zipaligning (4-byte page-align) {apk} -> {aligned_apk}...")
        cmd_align = [ZIPALIGN_EXE, "-f", "-p", "4", apk, aligned_apk]
        res = subprocess.run(cmd_align, capture_output=True, text=True)
        if res.returncode != 0:
            print(f"[-] Zipalign failed:\n{res.stderr}\n{res.stdout}")
            raise RuntimeError(f"Zipalign failed for {apk}")
        aligned_apks.append(aligned_apk)

    print("[*] Signing aligned APKs with shittim.keystore using uber-apk-signer (--skipZipAlign)...")
    signed_outputs = []
    for apk in aligned_apks:
        cmd_sign = [
            "java", "-jar", UBER_SIGNER_JAR,
            "-a", apk,
            "--ks", KEYSTORE_PATH,
            "--ksAlias", "shittim",
            "--ksPass", "123456",
            "--ksKeyPass", "123456",
            "--skipZipAlign",
            "-o", signed_dir
        ]
        res = subprocess.run(cmd_sign, capture_output=True, text=True)
        if res.returncode != 0:
            print(f"[-] Signing error on {apk}:\n{res.stderr}\n{res.stdout}")
            raise RuntimeError(f"Signing failed for {apk}")
        base_signed = os.path.splitext(os.path.basename(apk))[0] + "-signed.apk"
        signed_outputs.append(os.path.join(signed_dir, base_signed))
        print(f"[+] Signed {base_signed}")

    return signed_outputs


if __name__ == "__main__":
    unsigned_split = prepare_split_apk()
    unsigned_base = prepare_base_apk()
    signed = align_and_sign([unsigned_base, unsigned_split])
    print("\n[SUCCESS] Generated signed native redirected APKs:")
    for s in signed:
        print(f"  - {s}")
