using System.Diagnostics.CodeAnalysis;
using CommandLine;
using YGORogueDuelistCardModInstaller.Core;
using YGORogueDuelistCardModInstaller.Core.ModManager;

namespace YGORogueDuelistCardModInstaller;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("apply", isDefault: true, HelpText = "按当前启用列表与加载顺序注入全部 MOD（默认零侵入虚拟版本包，不改动游戏 DLL 和原版 ZIP）")]
public sealed class ApplyVerbOptions
{
    [Option('m', "mode", Required = false, HelpText = "注入模式：virtual（零侵入虚拟版本包，默认）或 manifest（启动器清单）")]
    public string? Mode { get; set; }

    [Option('i', "install-dir", Required = false, HelpText = "游戏安装目录（含 Yu-Gi-Oh! Rogue Duelist.dll）")]
    public string? InstallDir { get; set; }

    [Option('g', "game-dir", Required = false, HelpText = "游戏数据根目录或版本目录")]
    public string? GameDir { get; set; }

    [Option('p', "pics-dir", Required = false, HelpText = @"备用卡图目录（默认自动查找 YGOPro\pics）")]
    public string? PicsDir { get; set; }

    [Option('b', "build-only", Required = false, HelpText = "仅合成导出到指定目录，不改动游戏")]
    public string? BuildOnly { get; set; }

    [Option('l', "launch", Required = false, HelpText = "注入完成后直接启动游戏")]
    public bool Launch { get; set; }

    [Option("dev", Required = false, HelpText = "本次注入强制开启开发者模式（F10 调试面板 + window.YgoModDev + 解锁沙盒自由决斗）")]
    public bool DevMode { get; set; }

    [Option("ignore-version", Required = false, HelpText = "忽略 MOD 最低/最高游戏版本校验")]
    public bool IgnoreVersion { get; set; }

    [Option('y', "yes", Required = false, HelpText = "自动确认（如关闭正在运行的游戏）")]
    public bool AssumeYes { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录（含 mods\\，默认自动检测）")]
    public string? RootDir { get; set; }

    [Option("no-pause", Required = false, HelpText = "运行结束后不等待按回车退出")]
    public bool NoPause { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("list", HelpText = "列出所有已托管 MOD、启用状态、加载顺序、注入状态、版本校验与冲突检测")]
public sealed class ListVerbOptions
{
    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("import", HelpText = "导入单个 MOD 或工作区整合包（自动识别 workspace-pack.json、.zip 压缩包或文件夹）")]
public sealed class ImportVerbOptions
{
    [Value(0, MetaName = "source", Required = true, HelpText = "要导入的 .zip 文件或文件夹路径")]
    public string SourcePath { get; set; } = string.Empty;

    [Option('f', "force", Required = false, HelpText = "若同名 MOD 已存在则覆盖")]
    public bool Force { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("enable", HelpText = "启用指定 MOD（之后执行 apply 生效）")]
public sealed class EnableVerbOptions
{
    [Value(0, MetaName = "mod-id", Required = true, HelpText = "MOD ID（传入 all 启用全部）")]
    public string ModId { get; set; } = string.Empty;

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("disable", HelpText = "禁用指定 MOD（之后执行 apply 生效）")]
public sealed class DisableVerbOptions
{
    [Value(0, MetaName = "mod-id", Required = true, HelpText = "MOD ID（传入 all 禁用全部）")]
    public string ModId { get; set; } = string.Empty;

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("order", HelpText = "调整 MOD 加载顺序（序号从 1 开始，越靠后优先级越高）")]
public sealed class OrderVerbOptions
{
    [Value(0, MetaName = "mod-id", Required = true, HelpText = "MOD ID")]
    public string ModId { get; set; } = string.Empty;

    [Value(1, MetaName = "position", Required = true, HelpText = "目标顺序位置（1 表示最先加载，末尾表示最高优先级）")]
    public int Position { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("create", HelpText = "在 mods\\ 下新建一个标准 MOD 模板目录（支持 mod.lua/mod.js DSL 与模块化资产）")]
public sealed class CreateVerbOptions
{
    [Value(0, MetaName = "mod-id", Required = true, HelpText = "新 MOD 的唯一 ID（英文/数字/连字符）")]
    public string ModId { get; set; } = string.Empty;

    [Option('n', "name", Required = false, HelpText = "MOD 显示名称")]
    public string? Name { get; set; }

    [Option('a', "author", Required = false, HelpText = "作者名称")]
    public string? Author { get; set; }

    [Option('d', "desc", Required = false, HelpText = "MOD 简介")]
    public string? Description { get; set; }

    [Option("min-game-ver", Required = false, HelpText = "最低游戏版本要求（如 0.2.46）")]
    public string? MinGameVersion { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("remove", HelpText = "从 mods\\ 中删除指定 MOD")]
public sealed class RemoveVerbOptions
{
    [Value(0, MetaName = "mod-id", Required = true, HelpText = "要删除的 MOD ID")]
    public string ModId { get; set; } = string.Empty;

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("pack-mod", HelpText = "将单个 MOD 打包为可分发 .zip 文件")]
public sealed class PackModVerbOptions
{
    [Value(0, MetaName = "mod-id", Required = true, HelpText = "要打包的 MOD ID")]
    public string ModId { get; set; } = string.Empty;

    [Option('o', "output", Required = false, HelpText = "输出 .zip 路径（默认输出到 dist\\<modId>-v<ver>.zip）")]
    public string? OutputPath { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("pack-workspace", HelpText = "将整个 MOD 工作区（含全部/已启用 MOD、加载顺序、最低版本信息）打包为整合包 .zip")]
public sealed class PackWorkspaceVerbOptions
{
    [Option('o', "output", Required = false, HelpText = "输出 .zip 路径（默认输出到 dist\\<name>-v<ver>.zip）")]
    public string? OutputPath { get; set; }

    [Option('n', "name", Required = false, HelpText = "整合包名称（默认 YGO-Rogue-Modpack）")]
    public string? Name { get; set; }

    [Option('v', "version", Required = false, HelpText = "整合包版本号（默认 1.0.0）")]
    public string? Version { get; set; }

    [Option('d', "desc", Required = false, HelpText = "整合包简介")]
    public string? Description { get; set; }

    [Option("min-game-ver", Required = false, HelpText = "指定整合包最低游戏版本要求（未指定时自动取全部 MOD 的最高下限）")]
    public string? MinGameVersion { get; set; }

    [Option("enabled-only", Required = false, HelpText = "仅打包当前已启用的 MOD")]
    public bool EnabledOnly { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("import-workspace", HelpText = "一键导入工作区整合包 .zip（恢复所有 MOD 与加载顺序）")]
public sealed class ImportWorkspaceVerbOptions
{
    [Value(0, MetaName = "source", Required = true, HelpText = "工作区整合包 .zip 路径")]
    public string SourcePath { get; set; } = string.Empty;

    [Option('f', "force", Required = false, Default = true, HelpText = "覆盖已有同名 MOD（默认 true）")]
    public bool Force { get; set; } = true;

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("dev", HelpText = "查看或切换开发者模式（on / off / status）")]
public sealed class DevVerbOptions
{
    [Value(0, MetaName = "state", Required = false, Default = "status", HelpText = "on、off 或 status")]
    public string State { get; set; } = "status";

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("workspace", HelpText = "管理多个 MOD 工作区（list / new / add / use / remove）")]
public sealed class WorkspaceVerbOptions
{
    [Value(0, MetaName = "action", Required = false, Default = "list", HelpText = "操作：list | new | add | use | remove")]
    public string Action { get; set; } = "list";

    [Value(1, MetaName = "target", Required = false, HelpText = "工作区目录路径（new/add）或工作区 ID/名称（use/remove）")]
    public string? Target { get; set; }

    [Option('n', "name", Required = false, HelpText = "工作区显示名称")]
    public string? Name { get; set; }

    [Option('d', "desc", Required = false, HelpText = "工作区简介")]
    public string? Description { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("hot-reload", HelpText = "启动实时 MOD 热加载开发服务器（监听 mods\\ 变动并自动刷新游戏或浏览器预览）")]
public sealed class HotReloadVerbOptions
{
    [Option("port", Required = false, Default = ModHotReloadServer.DefaultPort, HelpText = "本地热加载端口（默认 17845）")]
    public int Port { get; set; } = ModHotReloadServer.DefaultPort;

    [Option("inject-game", Required = false, HelpText = "启动前先向游戏写入带热加载桥接的虚拟版本包")]
    public bool InjectGame { get; set; }

    [Option("open", Required = false, HelpText = "自动在默认浏览器打开实时热预览地址")]
    public bool OpenBrowser { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("restore", HelpText = "一键还原纯净原版（清理虚拟 MOD 包 + 还原可能修改过的启动器 DLL + 清理缓存）")]
public sealed class RestoreVerbOptions
{
    [Option('y', "yes", Required = false, HelpText = "若游戏正在运行则自动关闭")]
    public bool AssumeYes { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }

    [Option("no-pause", Required = false, HelpText = "运行结束后不等待按回车退出")]
    public bool NoPause { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("update-game", HelpText = "检查并更新原版游戏至 GitHub 最新 Release（不破坏 MOD 列表）")]
public sealed class UpdateGameVerbOptions
{
    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }

    [Option("no-pause", Required = false, HelpText = "运行结束后不等待按回车退出")]
    public bool NoPause { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("launch", HelpText = "直接启动游戏")]
public sealed class LaunchVerbOptions
{
    [Option('i', "install-dir", Required = false, HelpText = "游戏安装目录")]
    public string? InstallDir { get; set; }

    [Option("root", Required = false, HelpText = "MOD 管理器工作目录")]
    public string? RootDir { get; set; }
}

// Legacy compatibility verbs matching install-mod.ps1 & uninstall-mod.ps1
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("install", HelpText = "[兼容旧脚本] 直接按单包 install-mod.ps1 逻辑安装")]
public sealed class InstallVerbOptions
{
    [Option('i', "install-dir", Required = false, HelpText = "游戏安装目录（含 Yu-Gi-Oh! Rogue Duelist.dll）")]
    public string? InstallDir { get; set; }

    [Option('g', "game-dir", Required = false, HelpText = @"解包后的网页目录（默认 %LOCALAPPDATA%\Yu-Gi-Oh! Rogue Duelist\Game 下最新版本）")]
    public string? GameDir { get; set; }

    [Option('p', "pics-dir", Required = false, HelpText = @"卡图来源目录（默认自动查找 YGOPro\pics）")]
    public string? PicsDir { get; set; }

    [Option('b', "build-only", Required = false, HelpText = "只把生成结果写到该目录，不动游戏（测试用）")]
    public string? BuildOnly { get; set; }

    [Option('r', "repo", Required = false, Default = InstallOptions.DefaultRepo, HelpText = "GitHub 原版游戏仓库（owner/repo）")]
    public string Repo { get; set; } = InstallOptions.DefaultRepo;

    [Option("no-dll", Required = false, HelpText = "不修改启动器 DLL")]
    public bool NoDll { get; set; }

    [Option("root", Required = false, HelpText = @"MOD 安装包根目录（含 data\mod.json，默认自动检测）")]
    public string? RootDir { get; set; }

    [Option('y', "yes", Required = false, HelpText = "游戏版本与安装包不一致时自动继续")]
    public bool AssumeYes { get; set; }

    [Option("no-pause", Required = false, HelpText = "运行结束后不等待按回车退出")]
    public bool NoPause { get; set; }
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
[Verb("uninstall", HelpText = "[兼容旧脚本] 卸载 MOD（还原备份 DLL / 清理虚拟包并清除缓存）")]
public sealed class UninstallVerbOptions
{
    [Option('i', "install-dir", Required = false, HelpText = "游戏安装目录（含 Yu-Gi-Oh! Rogue Duelist.dll）")]
    public string? InstallDir { get; set; }

    [Option('g', "game-dir", Required = false, HelpText = "解包后的网页目录")]
    public string? GameDir { get; set; }

    [Option("root", Required = false, HelpText = @"MOD 安装包根目录（默认自动检测）")]
    public string? RootDir { get; set; }

    [Option("no-pause", Required = false, HelpText = "运行结束后不等待按回车退出")]
    public bool NoPause { get; set; }
}

public static class ArgNormalizer
{
    private static readonly Dictionary<string, string> PowerShellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-InstallDir"] = "--install-dir",
        ["-GameDir"] = "--game-dir",
        ["-PicsDir"] = "--pics-dir",
        ["-BuildOnly"] = "--build-only",
        ["-Repo"] = "--repo",
        ["-NoDll"] = "--no-dll",
        ["-Root"] = "--root",
        ["-Yes"] = "--yes",
        ["-NoPause"] = "--no-pause",
        ["-Mode"] = "--mode",
        ["-Launch"] = "--launch",
        ["-Dev"] = "--dev",
        ["-IgnoreVersion"] = "--ignore-version"
    };

    public static string[] Normalize(string[] args)
    {
        var list = new List<string>(args.Length);
        var forceUninstall = false;
        var hasLegacyNoDllOrRepo = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, "-Uninstall", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                forceUninstall = true;
                continue;
            }

            if (string.Equals(arg, "-NoDll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--no-dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-Repo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                hasLegacyNoDllOrRepo = true;
            }

            list.Add(PowerShellAliases.TryGetValue(arg, out var mapped) ? mapped : arg);
        }

        if (forceUninstall &&
            (list.Count == 0 || !string.Equals(list[0], "uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(0, "uninstall");
        }
        else if (hasLegacyNoDllOrRepo && list.Count > 0 && list[0].StartsWith('-'))
        {
            list.Insert(0, "install");
        }

        return list.ToArray();
    }
}
