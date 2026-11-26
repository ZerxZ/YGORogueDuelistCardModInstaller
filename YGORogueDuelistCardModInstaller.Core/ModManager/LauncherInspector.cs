using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace YGORogueDuelistCardModInstaller.Core.ModManager;

/// <summary>
/// Reads launcher metadata (InitialBundle, DesktopBoot, LauncherBuild) and
/// locates the embedded StartupHashes.json resource directly from PE metadata.
/// </summary>
public static class LauncherInspector
{
    public const string DefaultDesktopBoot =
        "const status = document.querySelector('#loading-status');\n" +
        "if (status) status.textContent = 'Preparing menu and shop artwork…';\n" +
        "const prepareArtwork = async () => {\n" +
        "  const response = await fetch('startup-assets.json');\n" +
        "  if (!response.ok) return;\n" +
        "  const {images} = await response.json();\n" +
        "  const paths = images.filter(path => /\\/(?:packs|characters|menu)\\//i.test(path)\n" +
        "    || /shopkeeper|shop[-_]|rogue-duelist-logo/i.test(path)).slice(0, 100);\n" +
        "  let next = 0;\n" +
        "  const until = performance.now() + 1800;\n" +
        "  await Promise.all(Array.from({length: 6}, async () => {\n" +
        "    while (performance.now() < until && next < paths.length) {\n" +
        "      const image = new Image();\n" +
        "      image.src = paths[next++];\n" +
        "      await image.decode().catch(() => {});\n" +
        "    }\n" +
        "  }));\n" +
        "};\n" +
        "await Promise.race([prepareArtwork().catch(console.warn), new Promise(resolve => setTimeout(resolve, 2200))]);\n" +
        "await import('./mobile.js');";

    public const string ReplayOriginalFragment =
        "uploaded:old?.steps===copy.responses.length&&old?.ended===copy.ended?old.uploaded:false,ended:copy.ended});";

    public const string ReplayAdaptedFragment =
        "uploaded:old?.steps===copy.responses.length&&old?.ended===copy.ended?old.uploaded:false,ended:copy.ended});void globalThis.ShadowDesktop?.storeReplay(id,stringify(copy)).catch(e=>console.warn('Replay file:',e.message));";

    public static LauncherMetadata? TryReadMetadata(string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
        {
            return null;
        }

        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var mr = pe.GetMetadataReader();
            string? version = null;
            string? fileName = null;
            string? sha256 = null;
            string? desktopBoot = null;
            var launcherBuild = 0;

            foreach (var th in mr.TypeDefinitions)
            {
                var td = mr.GetTypeDefinition(th);
                var typeName = mr.GetString(td.Name);
                if (typeName is not ("InitialBundle" or "GameFiles" or "LauncherUpdate"))
                {
                    continue;
                }

                foreach (var fh in td.GetFields())
                {
                    var fd = mr.GetFieldDefinition(fh);
                    if (!fd.Attributes.HasFlag(FieldAttributes.HasDefault))
                    {
                        continue;
                    }

                    var fieldName = mr.GetString(fd.Name);
                    var constant = mr.GetConstant(fd.GetDefaultValue());
                    var blob = mr.GetBlobReader(constant.Value);

                    if (typeName == "InitialBundle" && constant.TypeCode == ConstantTypeCode.String)
                    {
                        var str = blob.ReadUTF16(blob.Length);
                        if (fieldName == "Version") version = str;
                        else if (fieldName == "FileName") fileName = str;
                        else if (fieldName == "Sha256") sha256 = str;
                    }
                    else if (typeName == "GameFiles" && fieldName == "DesktopBoot" && constant.TypeCode == ConstantTypeCode.String)
                    {
                        desktopBoot = blob.ReadUTF16(blob.Length);
                    }
                    else if (typeName == "LauncherUpdate" && fieldName == "Build" && constant.TypeCode == ConstantTypeCode.Int32)
                    {
                        launcherBuild = blob.ReadInt32();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            return new LauncherMetadata(
                version,
                fileName ?? $"web-{version}.zip",
                sha256 ?? string.Empty,
                desktopBoot ?? DefaultDesktopBoot,
                launcherBuild);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Locates the exact byte offset and length of an embedded manifest resource
    /// inside a .NET assembly PE file (e.g. "StartupHashes.json").
    /// </summary>
    public static (int Offset, int Length)? TryLocateEmbeddedResource(byte[] dllBytes, string resourceName)
    {
        try
        {
            using var ms = new MemoryStream(dllBytes, writable: false);
            using var pe = new PEReader(ms);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            {
                return null;
            }

            var resDir = pe.PEHeaders.CorHeader.ResourcesDirectory;
            if (resDir.Size <= 0)
            {
                return null;
            }

            var sectionIndex = pe.PEHeaders.GetContainingSectionIndex(resDir.RelativeVirtualAddress);
            if (sectionIndex < 0)
            {
                return null;
            }

            var section = pe.PEHeaders.SectionHeaders[sectionIndex];
            var resBaseFileOffset = section.PointerToRawData + (resDir.RelativeVirtualAddress - section.VirtualAddress);

            var mr = pe.GetMetadataReader();
            foreach (var handle in mr.ManifestResources)
            {
                var res = mr.GetManifestResource(handle);
                if (!string.Equals(mr.GetString(res.Name), resourceName, StringComparison.Ordinal))
                {
                    continue;
                }

                var entryOffset = checked((int)(resBaseFileOffset + res.Offset));
                if (entryOffset < 0 || entryOffset + 4 > dllBytes.Length)
                {
                    return null;
                }

                var dataLength = BitConverter.ToInt32(dllBytes, entryOffset);
                var dataOffset = entryOffset + 4;
                if (dataLength <= 0 || dataOffset + dataLength > dllBytes.Length)
                {
                    return null;
                }

                return (dataOffset, dataLength);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string AdaptReplayText(string replaysJs)
    {
        if (replaysJs.Contains("ShadowDesktop?.storeReplay", StringComparison.Ordinal))
        {
            return replaysJs;
        }

        return replaysJs.Replace(ReplayOriginalFragment, ReplayAdaptedFragment, StringComparison.Ordinal);
    }

    public static byte[] AdaptReplayBytes(byte[] replaysJsBytes)
    {
        var text = Encoding.UTF8.GetString(replaysJsBytes);
        return ModPatcher.GetUtf8Bytes(AdaptReplayText(text));
    }
}
