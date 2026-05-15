using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace XboxStartupEnabler;

public enum PatchStateValue { Original, Patched, Unknown }

public sealed record SiteState(string Description, int FileOffset, PatchStateValue State);

public sealed record TargetSummary(
    string FileName,
    string FilePath,
    string? Version,
    long Size,
    bool Exists,
    IReadOnlyList<SiteState> Sites);

public sealed record SystemSummary(
    IReadOnlyList<TargetSummary> Targets)
{
    public int Total => Targets.Sum(t => t.Sites.Count);
    public int Patched => Targets.Sum(t => t.Sites.Count(s => s.State == PatchStateValue.Patched));
    public int Original => Targets.Sum(t => t.Sites.Count(s => s.State == PatchStateValue.Original));
    public int Unknown => Targets.Sum(t => t.Sites.Count(s => s.State == PatchStateValue.Unknown));
    public bool AllPatched => Total > 0 && Patched == Total;
    public bool AllOriginal => Total > 0 && Original == Total;
    public bool AllExist => Targets.All(t => t.Exists);
}

public static class Patcher
{
    public const string GameModeDll = "gamemode.dll";
    public const string SettingsGamingDll = "SettingsHandlers_Gaming.dll";
    public const string TwinuiDll = "twinui.pcshell.dll";

    public static readonly string System32 =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

    public static string AppRoot =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

    public static string BackupRoot => Path.Combine(AppRoot, "backups");

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Inspection

    public static SystemSummary Inspect(string? targetDir = null)
    {
        targetDir ??= System32;
        var summaries = new List<TargetSummary>();
        foreach (var t in BuildTargets(targetDir))
        {
            if (!File.Exists(t.FilePath))
            {
                summaries.Add(new TargetSummary(t.FileName, t.FilePath, null, 0, false, Array.Empty<SiteState>()));
                continue;
            }
            var bytes = File.ReadAllBytes(t.FilePath);
            var info = FileVersionInfo.GetVersionInfo(t.FilePath);
            List<SiteState> sites = new();
            try
            {
                foreach (var p in t.PatchFactory(bytes))
                    sites.Add(new SiteState(p.Description, p.FileOffset, p.DetectState(bytes)));
            }
            catch (Exception ex)
            {
                sites.Add(new SiteState($"(error resolving patches: {ex.Message})", 0, PatchStateValue.Unknown));
            }
            summaries.Add(new TargetSummary(
                t.FileName, t.FilePath, info.FileVersion, new FileInfo(t.FilePath).Length, true, sites));
        }
        return new SystemSummary(summaries);
    }

    // Apply

    public static void Apply(string? targetDir = null, Action<string>? log = null)
    {
        targetDir ??= System32;
        log ??= _ => { };
        log($"Target directory: {targetDir}");
        log("Pre-flight check...");
        Directory.CreateDirectory(BackupRoot);

        var targets = BuildTargets(targetDir);

        // Pre-flight: every patch must be locatable and in a known state
        foreach (var t in targets)
        {
            if (!File.Exists(t.FilePath))
                throw new FileNotFoundException($"target DLL not found: {t.FilePath}");
            var bytes = File.ReadAllBytes(t.FilePath);
            var patches = t.PatchFactory(bytes).ToList();
            if (patches.Count == 0)
                throw new Exception($"no patches resolved for {t.FileName}");
            foreach (var p in patches)
            {
                var state = p.DetectState(bytes);
                if (state == PatchStateValue.Unknown)
                    throw new Exception($"refusing to patch {t.FileName}: bytes at 0x{p.FileOffset:X} match neither original nor patched layout — DLL version may be unsupported.");
                log($"  [{state,-8}] {t.FileName}: {p.Description}");
            }
        }

        foreach (var t in targets)
            ApplyOne(t, log);
    }

    public static void Restore(string? targetDir = null, Action<string>? log = null)
    {
        targetDir ??= System32;
        log ??= _ => { };
        log("Restoring originals from backup...");
        foreach (var t in BuildTargets(targetDir))
            RestoreOne(t, log);
    }

    // Per-target ops

    private static void ApplyOne(Target t, Action<string> log)
    {
        var bytes = File.ReadAllBytes(t.FilePath);
        var patches = t.PatchFactory(bytes).ToList();

        if (patches.All(p => p.DetectState(bytes) == PatchStateValue.Patched))
        {
            log($"  {t.FileName}: already fully patched, skipping.");
            return;
        }

        var info = FileVersionInfo.GetVersionInfo(t.FilePath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var backupName = $"{t.FileName}.{info.FileVersion?.Replace(' ', '_')}.{sha}.bak";
        var backupPath = Path.Combine(BackupRoot, backupName);
        if (!File.Exists(backupPath))
        {
            File.WriteAllBytes(backupPath, bytes);
            log($"  backup → {backupPath}");
        }
        else
        {
            log($"  backup already exists: {backupPath}");
        }
        File.WriteAllText(Path.Combine(BackupRoot, $"{t.FileName}.latest.txt"), backupName);

        var patched = (byte[])bytes.Clone();
        foreach (var p in patches)
        {
            if (p.DetectState(bytes) == PatchStateValue.Patched)
            {
                log($"    skip (already patched): {p.Description}");
                continue;
            }
            Array.Copy(p.Replacement, 0, patched, p.FileOffset, p.Replacement.Length);
            log($"    patched: {p.Description}  @ 0x{p.FileOffset:X}");
        }

        UpdatePeChecksum(patched);
        WriteSystemFile(t.FilePath, patched);
    }

    private static void RestoreOne(Target t, Action<string> log)
    {
        var pointer = Path.Combine(BackupRoot, $"{t.FileName}.latest.txt");
        if (!File.Exists(pointer))
        {
            log($"  {t.FileName}: no backup recorded, skipping.");
            return;
        }
        var backupName = File.ReadAllText(pointer).Trim();
        var backupPath = Path.Combine(BackupRoot, backupName);
        if (!File.Exists(backupPath))
        {
            log($"  {t.FileName}: backup file missing: {backupPath}");
            return;
        }
        var backupBytes = File.ReadAllBytes(backupPath);
        WriteSystemFile(t.FilePath, backupBytes);
        log($"  {t.FileName}: restored from {backupName}");
    }

    // ──────────────────────────────────────────── filesystem & PE primitives

    private static void WriteSystemFile(string path, byte[] bytes)
    {
        bool isSystem32 = string.Equals(
            Path.GetFullPath(Path.GetDirectoryName(path)!),
            Path.GetFullPath(System32), StringComparison.OrdinalIgnoreCase);

        if (isSystem32)
        {
            try { Run("takeown.exe", $"/F \"{path}\" /A"); }
            catch (Exception ex) { throw new Exception($"takeown failed on {path}: {ex.Message}", ex); }
            try { Run("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /C"); }
            catch (Exception ex) { throw new Exception($"icacls /grant failed on {path}: {ex.Message}", ex); }
        }

        // Rename-then-write strategy. Trying to overwrite a loaded DLL with File.Move(overwrite: true) fails
        // with Access Denied if any process has it mapped without FILE_SHARE_DELETE on the directory entry.
        // Renaming the *directory entry* succeeds because the loaded image doesn't care about its path.
        // Then we drop the patched bytes at the original path and schedule the stale rename for deletion on next reboot.
        var stash = $"{path}.old-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, stash);
        }
        catch (Exception ex)
        {
            throw new Exception(
                $"Could not rename '{Path.GetFileName(path)}' out of the way (the loaded DLL is locked): {ex.Message}", ex);
        }

        try
        {
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            // Try to put the original back if writing failed
            try { File.Move(stash, path); } catch { }
            throw new Exception($"Wrote new bytes but failed: {ex.Message}", ex);
        }

        if (isSystem32)
        {
            // Schedule the stash (loaded copy of the old bytes) for delete on next boot.
            // Only do this for System32 because PendingFileRenameOperations is global.
            try { MoveFileEx(stash, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }

            try { Run("icacls.exe", $"\"{path}\" /setowner \"NT SERVICE\\TrustedInstaller\" /C", ignoreExitCode: true); } catch { }
        }
        else
        {
            // sandbox / dry-run: delete the stash immediately (no loaded image to worry about)
            try { File.Delete(stash); } catch { }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    private static void Run(string exe, string args, bool ignoreExitCode = false)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && !ignoreExitCode)
            throw new Exception($"{exe} {args} → exit {p.ExitCode}\n{stdout}\n{stderr}");
    }

    private static void UpdatePeChecksum(byte[] image)
    {
        int peOff = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(0x3C, 4));
        int optHdrOff = peOff + 24;
        int checksumOff = optHdrOff + 64;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(checksumOff, 4), 0);
        uint sum = 0;
        for (int i = 0; i + 1 < image.Length; i += 2)
        {
            ushort w = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(i, 2));
            sum += w;
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        if ((image.Length & 1) != 0)
        {
            sum += image[^1];
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        sum = (sum & 0xFFFF) + (sum >> 16);
        sum &= 0xFFFF;
        uint final = sum + (uint)image.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(checksumOff, 4), final);
    }

    // Target catalog

    private sealed record Target(string FileName, string FilePath, Func<byte[], IEnumerable<Patch>> PatchFactory);

    private static List<Target> BuildTargets(string targetDir) => new()
    {
        new Target(GameModeDll, Path.Combine(targetDir, GameModeDll), BuildGameModePatches),
        new Target(SettingsGamingDll, Path.Combine(targetDir, SettingsGamingDll), BuildHandheldCheckPatches),
        new Target(TwinuiDll, Path.Combine(targetDir, TwinuiDll), BuildHandheldCheckPatches),
    };

    // gamemode.dll: replace prologues of IsSupported/CanSet with `mov eax,1;ret`,
    // and NOP the home-app `jz` in SetGamingFullScreenExperience so the setter runs.
    private static IEnumerable<Patch> BuildGameModePatches(byte[] dll)
    {
        var pe = new PeImage(dll);
        var stubBytes = new byte[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 };           // mov eax,1 ; ret
        var stubPattern = new int?[] { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 };

        int isSupportedOff = pe.RvaToFileOffset(pe.GetExportRva("IsGamingFullScreenExperienceSupported"));
        yield return new Patch(
            "IsGamingFullScreenExperienceSupported → return TRUE", isSupportedOff,
            new int?[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48 }, stubBytes, stubPattern);

        int canSetOff = pe.RvaToFileOffset(pe.GetExportRva("CanSetGamingFullScreenExperience"));
        yield return new Patch(
            "CanSetGamingFullScreenExperience → return TRUE", canSetOff,
            new int?[] { 0x48, 0x89, 0x5C, 0x24, 0x20, 0x57 }, stubBytes, stubPattern);

        int setFuncOff = pe.RvaToFileOffset(pe.GetExportRva("SetGamingFullScreenExperience"));
        int jzOff = -1;
        for (int i = setFuncOff; i < setFuncOff + 0x80 && i + 8 <= dll.Length; i++)
        {
            if (dll[i] != 0x84 || dll[i + 1] != 0xC0) continue;     // test al,al
            bool original = dll[i + 2] == 0x0F && dll[i + 3] == 0x84;
            bool patched  = dll[i + 2] == 0x90 && dll[i + 3] == 0x90
                         && dll[i + 4] == 0x90 && dll[i + 5] == 0x90
                         && dll[i + 6] == 0x90 && dll[i + 7] == 0x90;
            if (original || patched) { jzOff = i + 2; break; }
        }
        if (jzOff < 0)
            throw new Exception("could not locate the home-app JZ in SetGamingFullScreenExperience");
        yield return new Patch(
            "SetGamingFullScreenExperience: NOP home-app gate JZ", jzOff,
            new int?[] { 0x0F, 0x84, null, null, null, null },
            new byte[]  { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 },
            new int?[] { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
    }

    // Generic handheld-check patcher used for any DLL that contains `cmp [rsp+disp8], 0x2E ; setcc reg`  (0x2E = DEVICEFAMILYDEVICEFORM_GAMING_HANDHELD).
    // Found in SettingsHandlers_Gaming.dll (UI applicability) and in twinui.pcshell.dll (the `GamingPostureConfiguration` WinRT class that the Settings GET handler reads).
    private static IEnumerable<Patch> BuildHandheldCheckPatches(byte[] dll)
    {
        const int len = 8;
        int idx = 0;
        for (int off = 0; off <= dll.Length - len; off++)
        {
            bool isOriginal = MatchesAt(dll, off,
                new int?[] { 0x83, 0x7C, 0x24, null, 0x2E, 0x0F, 0x94, null });
            bool isPatched  = (dll[off] >= 0xB0 && dll[off] <= 0xB7)
                && MatchesAt(dll, off + 1,
                    new int?[] { 0x01, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
            if (!isOriginal && !isPatched) continue;

            byte reg = isOriginal
                ? (byte)(dll[off + 7] & 0x07)
                : (byte)(dll[off]     & 0x07);
            byte movOpcode = (byte)(0xB0 + reg);

            yield return new Patch(
                $"force handheld form-factor check #{++idx} (reg={reg})", off,
                new int?[] { 0x83, 0x7C, 0x24, null, 0x2E, 0x0F, 0x94, 0xC0 | reg },
                new byte[]  { movOpcode, 0x01, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 },
                new int?[] { movOpcode, 0x01, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 });
            off += len - 1;
        }
        if (idx == 0)
            throw new Exception("no `cmp [rsp+disp8], 0x2E ; sete` patterns found in this DLL.");
    }

    private static bool MatchesAt(byte[] dll, int off, int?[] pattern)
    {
        if (off < 0 || off + pattern.Length > dll.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i].HasValue && dll[off + i] != pattern[i]!.Value) return false;
        return true;
    }
}

// Data types & PE reader

internal sealed class Patch
{
    public string Description { get; }
    public int FileOffset { get; }
    public int?[] ExpectedOriginal { get; }
    public byte[] Replacement { get; }
    public int?[] PatchedSignature { get; }

    public Patch(string description, int fileOffset,
                 int?[] expectedOriginal, byte[] replacement, int?[] patchedSignature)
    {
        Description = description;
        FileOffset = fileOffset;
        ExpectedOriginal = expectedOriginal;
        Replacement = replacement;
        PatchedSignature = patchedSignature;
    }

    public PatchStateValue DetectState(byte[] dll)
    {
        if (Matches(dll, FileOffset, PatchedSignature)) return PatchStateValue.Patched;
        if (Matches(dll, FileOffset, ExpectedOriginal)) return PatchStateValue.Original;
        return PatchStateValue.Unknown;
    }

    private static bool Matches(byte[] dll, int off, int?[] pattern)
    {
        if (off < 0 || off + pattern.Length > dll.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i].HasValue && dll[off + i] != pattern[i]!.Value) return false;
        return true;
    }
}

internal sealed class PeImage
{
    private readonly byte[] _bytes;
    private readonly int _peOff;
    private readonly bool _isPe32Plus;
    private readonly (uint VAddr, uint VSize, uint RAddr, uint RSize)[] _sections;
    private readonly int _exportRva;

    public PeImage(byte[] bytes)
    {
        _bytes = bytes;
        _peOff = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C, 4));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(_peOff, 4)) != 0x00004550)
            throw new InvalidDataException("not a PE file");
        int coff = _peOff + 4;
        int nSections = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(coff + 2, 2));
        int sizeOptHdr = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(coff + 16, 2));
        int opt = coff + 20;
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(opt, 2));
        _isPe32Plus = magic == 0x20B;
        int dirOff = opt + (_isPe32Plus ? 112 : 96);
        _exportRva = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(dirOff, 4));
        int secOff = opt + sizeOptHdr;
        _sections = new (uint, uint, uint, uint)[nSections];
        for (int i = 0; i < nSections; i++)
        {
            int s = secOff + i * 40;
            _sections[i] = (
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 12, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 20, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(s + 16, 4)));
        }
    }

    public int RvaToFileOffset(int rva)
    {
        foreach (var s in _sections)
        {
            uint span = Math.Max(s.VSize, s.RSize);
            if ((uint)rva >= s.VAddr && (uint)rva < s.VAddr + span)
                return (int)((uint)rva - s.VAddr + s.RAddr);
        }
        throw new ArgumentOutOfRangeException(nameof(rva), $"RVA 0x{rva:X} not mapped");
    }

    public int GetExportRva(string name)
    {
        int eOff = RvaToFileOffset(_exportRva);
        uint nNames = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(eOff + 0x18, 4));
        uint funcsRva = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(eOff + 0x1C, 4));
        uint namesRva = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(eOff + 0x20, 4));
        uint ordRva = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(eOff + 0x24, 4));
        int funcsOff = RvaToFileOffset((int)funcsRva);
        int namesOff = RvaToFileOffset((int)namesRva);
        int ordOff = RvaToFileOffset((int)ordRva);
        for (int i = 0; i < nNames; i++)
        {
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(namesOff + i * 4, 4));
            int nameOff = RvaToFileOffset((int)nameRva);
            int end = nameOff;
            while (_bytes[end] != 0) end++;
            string n = Encoding.ASCII.GetString(_bytes, nameOff, end - nameOff);
            if (n == name)
            {
                ushort ord = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(ordOff + i * 2, 2));
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(funcsOff + ord * 4, 4));
            }
        }
        throw new KeyNotFoundException($"export `{name}` not found");
    }
}
