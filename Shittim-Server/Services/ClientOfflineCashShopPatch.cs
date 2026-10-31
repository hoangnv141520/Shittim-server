using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Shittim_Server.Services;

// The offline Steam catalogue is empty. Keep purchases on the local gateway instead
// of asking the SDK to resolve a real-money product (which returns 100211).
internal static class ClientOfflineCashShopPatch
{
    private const int BodyOffset = 0x87;
    private const int BodyLength = 0xD9;
    private const string StateSuffix = ".shittim_offline_cash_shop_patch.json";
    private static readonly byte?[] Entry = Pattern(
        "41 57 41 56 56 57 55 53 48 83 EC 28 44 89 CD 44 89 C3 48 89 D6 " +
        "48 8B BC 24 88 00 00 00 44 0F B6 B4 24 80 00 00 00 80 3D ?? ?? ?? ?? 00 75 58");
    private static readonly byte?[] VerifyCall = Pattern(
        "4C 8B 40 38 48 8B 50 20 4C 8B 48 28 0F 10 40 48 0F B6 40 58 " +
        "88 44 24 30 0F 11 44 24 20 48 89 F9 E8 ?? ?? ?? ??");
    private static readonly byte?[] OriginalStart = Pattern(
        "40 88 69 10 44 88 71 11 48 89 79 18 48 83 C1 18 48 89 FA E8 ?? ?? ?? ??");
    private static readonly byte?[] OriginalEnd = Pattern(
        "4C 8B 8C 24 90 00 00 00 48 8B 01 48 8B 90 70 03 00 00 " +
        "48 89 54 24 20 48 89 F2 41 89 D8 FF 90 68 03 00 00");
    private static readonly byte?[] Epilogue = Pattern("90 48 83 C4 28 5B 5D 5F 5E 41 5E 41 5F C3");
    private static readonly byte?[] VerifyEntry = Pattern(
        "41 57 41 56 41 55 41 54 56 57 55 53 48 83 EC 28 4D 89 CE 4D 89 C7 " +
        "49 89 D4 48 89 CF 0F B6 AC 24 A0 00 00 00");

    public static void Apply(string path, ILogger logger)
    {
        var data = File.ReadAllBytes(path);
        var statePath = path + StateSuffix;
        var saved = File.Exists(statePath)
            ? JsonSerializer.Deserialize<PatchState>(File.ReadAllText(statePath))
            : null;
        if (!TryPlan(data, saved, out var plan))
        {
            logger.LogWarning("Offline cash-shop patch signatures did not match; leaving the purchase flow unchanged");
            return;
        }

        if (Matches(data, plan!.Offset, plan.Patched))
        {
            logger.LogInformation("Offline cash-shop patch already applied: {Path}", path);
            return;
        }

        // Save exact original instructions before touching the DLL. A second startup
        // must keep those originals, and a Steam update must never inherit old bytes.
        var tempPath = statePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(plan));
        File.Move(tempPath, statePath, true);
        Write(path, plan.Offset, plan.Patched);
        logger.LogInformation("Applied offline cash-shop patch: {Path}", path);
    }

    public static void Revert(string path, ILogger logger)
    {
        var statePath = path + StateSuffix;
        if (!File.Exists(statePath)) return;
        var saved = JsonSerializer.Deserialize<PatchState>(File.ReadAllText(statePath));
        if (saved == null) throw new InvalidDataException("Empty offline cash-shop patch state");
        var data = File.ReadAllBytes(path);
        if (!Matches(data, saved.Offset, saved.Patched))
        {
            if (Matches(data, saved.Offset, saved.Original))
                File.Delete(statePath);
            else
                logger.LogWarning("Offline cash-shop patch state does not match this DLL; no restore bytes were written");
            return;
        }
        if (!TryPlan(data, saved, out _))
            throw new InvalidDataException("Offline cash-shop patch identity no longer matches");
        Write(path, saved.Offset, saved.Original);
        File.Delete(statePath);
        logger.LogInformation("Reverted offline cash-shop patch: {Path}", path);
    }

    internal static bool TryPlan(byte[] data, PatchState? saved, out PatchState? plan)
    {
        plan = null;
        var entries = Find(data, Entry);
        var calls = Find(data, VerifyCall);
        if (entries.Count != 1 || calls.Count != 1) return false;
        var offset = entries[0] + BodyOffset;
        if (!Matches(data, offset - 10, Pattern("4C 89 F9 C6 05 ?? ?? ?? ?? 01"))) return false;
        if (!Matches(data, offset + BodyLength, Epilogue)) return false;
        var original = saved != null && saved.Offset == offset && Matches(data, offset, saved.Patched)
            ? saved.Original
            : data.AsSpan(offset, BodyLength).ToArray();
        if (original.Length != BodyLength || !Matches(original, 0, OriginalStart)
            || !Matches(original, BodyLength - OriginalEnd.Length, OriginalEnd)) return false;

        byte[] patched;
        try
        {
            using var reader = new PEReader(new MemoryStream(data, false));
            // IL2CPP bodies and runtime helpers can live in different executable
            // sections. Translate each file offset before computing rel32 calls.
            int Rva(int position, int length)
            {
                foreach (var section in reader.PEHeaders.SectionHeaders)
                    if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0
                        && Inside(section, position, length))
                        return checked(section.VirtualAddress + position - section.PointerToRawData);
                throw new BadImageFormatException("Cash-shop target is outside executable sections");
            }
            int FileOffset(int rva, int length)
            {
                foreach (var section in reader.PEHeaders.SectionHeaders)
                    if ((section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0
                        && rva >= section.VirtualAddress
                        && (long)rva + length <= (long)section.VirtualAddress + section.SizeOfRawData)
                        return checked(section.PointerToRawData + rva - section.VirtualAddress);
                throw new BadImageFormatException("Cash-shop call target is outside executable sections");
            }
            var verifyCall = calls[0] + 32;
            var verifyRva = checked(Rva(verifyCall, 5) + 5
                + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(verifyCall + 1, 4)));
            var barrierRva = checked(Rva(offset + 19, 5) + 5
                + BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(20, 4)));
            if (!Matches(data, FileOffset(verifyRva, VerifyEntry.Length), VerifyEntry)) return false;
            FileOffset(barrierRva, 1);
            patched = BuildRedirect(Rva(offset, BodyLength), verifyRva, barrierRva);
        }
        catch (BadImageFormatException) { return false; }

        plan = new PatchState { Offset = offset, Original = original,
            Patched = patched };
        return saved == null || !Matches(data, offset, saved.Patched)
            || saved.Patched.AsSpan().SequenceEqual(plan.Patched);
    }

    internal static byte[] BuildRedirect(int offset, int verify, int barrier)
    {
        var code = new List<byte>();
        void Emit(string hex) => code.AddRange(Convert.FromHexString(hex.Replace(" ", "")));
        void Call(int target)
        {
            code.Add(0xE8);
            code.AddRange(BitConverter.GetBytes(checked(target - (offset + code.Count + 4))));
        }

        // At this point the original prologue has preserved the caller's registers:
        // rcx=this, rsi=ShopCashId, bl=teen, bpl=showRewardUI, r14b=refreshUI,
        // rdi=purchasedCallback. Keep the original managed write barrier.
        Emit("49 89 CF 40 88 69 10 44 88 71 11 48 89 79 18 48 83 C1 18 48 89 FA");
        Call(barrier);
        Emit("48 83 EC 20"); // room for seven arguments plus the hidden MethodInfo
        Emit("48 C7 44 24 20 00 00 00 00 48 C7 44 24 28 00 00 00 00");
        Emit("0F B6 C3 48 89 44 24 30 48 C7 44 24 38 00 00 00 00");
        Emit("31 D2"); // NpSN=0: local purchase, no publisher identity needed
        // Selection packs need their selection payload. Signal an explicit server
        // rejection instead of silently granting the base pack without the choice.
        Emit("48 8B 84 24 B0 00 00 00 48 85 C0 74 0D 83 78 18 00 7E 07 48 C7 C2 FF FF FF FF");
        Emit("4C 89 F9 45 31 C0 49 89 F1"); // this, stamp=null, ShopCashId
        Call(verify);
        Emit("48 83 C4 20");
        // Fall through to the unchanged epilogue; do not enter the Steam SDK.
        while (code.Count < BodyLength) code.Add(0x90);
        if (code.Count != BodyLength) throw new InvalidOperationException("Cash-shop redirect exceeds its instruction slot");
        return code.ToArray();
    }

    private static bool Inside(SectionHeader s, int offset, int length) =>
        offset >= s.PointerToRawData && (long)offset + length <= (long)s.PointerToRawData + s.SizeOfRawData;

    private static void Write(string path, int offset, byte[] bytes)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Position = offset;
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static byte?[] Pattern(string hex) => hex.Split(' ')
        .Select(x => x == "??" ? (byte?)null : Convert.ToByte(x, 16)).ToArray();

    private static bool Matches(byte[] data, int offset, byte[] bytes) =>
        offset >= 0 && (long)offset + bytes.Length <= data.Length
        && data.AsSpan(offset, bytes.Length).SequenceEqual(bytes);

    private static bool Matches(byte[] data, int offset, byte?[] pattern) =>
        offset >= 0 && (long)offset + pattern.Length <= data.Length
        && pattern.Select((b, i) => !b.HasValue || data[offset + i] == b.Value).All(x => x);

    private static List<int> Find(byte[] data, byte?[] pattern)
    {
        var matches = new List<int>();
        var start = 0;
        while (start <= data.Length - pattern.Length)
        {
            var index = data.AsSpan(start).IndexOf(pattern[0]!.Value);
            if (index < 0) break;
            start += index;
            if (Matches(data, start, pattern)) matches.Add(start);
            start++;
        }
        return matches;
    }

    internal sealed class PatchState
    {
        public int Offset { get; set; }
        public byte[] Original { get; set; } = [];
        public byte[] Patched { get; set; } = [];
    }
}
