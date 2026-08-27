using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace SourceGit
{
    public partial class App : Application
    {
        #region App Entry Point
        [STAThread]
        public static void Main(string[] args)
        {
            Native.OS.SetupBasicDirectories();

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Native.OS.LogException(e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved();
            };

            try
            {
                if (TryLaunchAsRebaseTodoEditor(args, out int exitTodo))
                    Environment.Exit(exitTodo);
                else if (TryLaunchAsRebaseMessageEditor(args, out int exitMessage))
                    Environment.Exit(exitMessage);
                else
                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Native.OS.LogException(ex);
            }
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            var builder = AppBuilder.Configure<App>();
            builder.UsePlatformDetect();
            builder.LogToTrace();
            builder.WithInterFont();
            builder.With(new FontManagerOptions()
            {
                DefaultFamilyName = "fonts:Inter#Inter"
            });
            builder.ConfigureFonts(manager =>
            {
                var monospace = new EmbeddedFontCollection(
                    new Uri("fonts:SourceGit", UriKind.Absolute),
                    new Uri("avares://SourceGit/Resources/Fonts", UriKind.Absolute));
                manager.AddFontCollection(monospace);
            });

            Native.OS.SetupApp(builder);
            return builder;
        }
        #endregion

        #region Utility Functions
        public static async Task<bool> AskConfirmAsync(string message, Models.ConfirmButtonType buttonType = Models.ConfirmButtonType.OkCancel)
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            {
                var confirm = new Views.Confirm();
                confirm.SetData(message, buttonType);
                return await confirm.ShowDialog<bool>(owner);
            }

            return false;
        }

        public static async Task<Models.ConfirmEmptyCommitResult> AskConfirmEmptyCommitAsync(bool hasLocalChanges, bool hasSelectedUnstaged)
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
            {
                var confirm = new Views.ConfirmEmptyCommit();
                confirm.TxtMessage.Text = Text(hasLocalChanges ? "ConfirmEmptyCommit.WithLocalChanges" : "ConfirmEmptyCommit.NoLocalChanges");
                confirm.BtnStageAllAndCommit.IsVisible = hasLocalChanges;
                confirm.BtnStageSelectedAndCommit.IsVisible = hasSelectedUnstaged;
                return await confirm.ShowDialog<Models.ConfirmEmptyCommitResult>(owner);
            }

            return Models.ConfirmEmptyCommitResult.Cancel;
        }

        public static void SetLocale(string localeKey)
        {
            var locale = Models.Locale.Supported.Find(x => x.Key.Equals(localeKey, StringComparison.OrdinalIgnoreCase));
            var finalLocaleKey = locale?.Key ?? "en_US";

            if (Current is not App app ||
                app.Resources[finalLocaleKey] is not ResourceDictionary targetLocale ||
                targetLocale == app._activeLocale)
                return;

            if (app._activeLocale != null)
                app.Resources.MergedDictionaries.Remove(app._activeLocale);

            app.Resources.MergedDictionaries.Add(targetLocale);
            app._activeLocale = targetLocale;
        }

        public static void SetTheme(string theme, string themeOverridesFile)
        {
            if (Current is not App app)
                return;

            if (theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
                app.RequestedThemeVariant = ThemeVariant.Light;
            else if (theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
                app.RequestedThemeVariant = ThemeVariant.Dark;
            else
                app.RequestedThemeVariant = ThemeVariant.Default;

            if (app._themeOverrides != null)
            {
                app.Resources.MergedDictionaries.Remove(app._themeOverrides);
                app._themeOverrides = null;
            }

            if (!string.IsNullOrEmpty(themeOverridesFile) && File.Exists(themeOverridesFile))
            {
                try
                {
                    var resDic = new ResourceDictionary();
                    using var stream = File.OpenRead(themeOverridesFile);
                    var overrides = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.ThemeOverrides);
                    foreach (var kv in overrides.BasicColors)
                    {
                        if (kv.Key.Equals("SystemAccentColor", StringComparison.Ordinal))
                            resDic["SystemAccentColor"] = kv.Value;
                        else
                            resDic[$"Color.{kv.Key}"] = kv.Value;
                    }

                    // DisableSubtleHover 优先级最高：只要 true，就把 Color.SubtleHover 强制透明，等价于关闭 hover 底色
                    // （这样不会影响用户同时在 BasicColors 里写 SubtleHover：true 时总是优先透明）
                    if (overrides.DisableSubtleHover == true)
                        resDic["Color.SubtleHover"] = Colors.Transparent;

                    // 覆盖悬浮过渡时长（毫秒 → TimeSpan）
                    if (overrides.HoverTransitionMs.HasValue)
                    {
                        var ms = overrides.HoverTransitionMs.Value;
                        if (ms < 0) ms = 0;
                        if (ms > 5000) ms = 5000;
                        resDic["Duration.Hover"] = TimeSpan.FromMilliseconds(ms);
                    }

                    // 覆盖悬浮过渡缓动函数。识别常用缓动的类名（大小写不敏感，可省略 "Ease" 后缀）
                    if (!string.IsNullOrEmpty(overrides.HoverTransitionEasing))
                    {
                        var easing = CreateEasingByName(overrides.HoverTransitionEasing);
                        if (easing != null) resDic["Easing.Hover"] = easing;
                    }

                    if (overrides.GraphColors.Count > 0)
                        Models.CommitGraph.SetPens(overrides.GraphColors, overrides.GraphPenThickness);
                    else
                        Models.CommitGraph.SetDefaultPens(overrides.GraphPenThickness);

                    // 按当前 SystemAccentColor 亮度自动反算选中前景色（用户未显式指定时）
                    EnsureListItemSelectedForeground(app, resDic);

                    app.Resources.MergedDictionaries.Add(resDic);
                    app._themeOverrides = resDic;
                }
                catch
                {
                    // ignore
                }
            }
            else
            {
                Models.CommitGraph.SetDefaultPens();

                // 即使没加载外置主题，也按系统强调色反算一次选中前景色
                var auto = new ResourceDictionary();
                EnsureListItemSelectedForeground(app, auto);
                if (auto.Count > 0) app.Resources.MergedDictionaries.Add(auto);
            }
        }

        private static void EnsureListItemSelectedForeground(Application app, ResourceDictionary resDic)
        {
            // 用户已显式指定 BasicColors["ListItem.Selected.Fore"]，完全尊重
            if (resDic.ContainsKey("Color.ListItem.Selected.Fore")) return;

            // 收集选中背景可能用到的颜色，逐一计算亮度，取最亮的那个作为对比依据
            // （因为 SystemListLowColor 常是 Accent 的半透明版本，需要把 alpha 考虑进去）
            var candidates = new List<Color>();
            var accent = ResolveColor(app, resDic, "SystemAccentColor");
            var listLow = ResolveColor(app, resDic, "SystemListLowColor");
            var listMedium = ResolveColor(app, resDic, "SystemListMediumColor");

            if (accent.HasValue) candidates.Add(accent.Value);
            if (listLow.HasValue) candidates.Add(listLow.Value);
            if (listMedium.HasValue) candidates.Add(listMedium.Value);

            if (candidates.Count == 0) return;

            // 计算每个颜色 WCAG 相对亮度（sRGB → 线性 → 加权）
            var maxRelativeLuminance = 0.0;
            foreach (var c in candidates)
            {
                var lum = GetRelativeLuminance(c);
                if (lum > maxRelativeLuminance) maxRelativeLuminance = lum;
            }

            // WCAG 阈值：相对亮度 0.179 是区分"深底"和"浅底"的分界
            // 暗背景 → 白字（#FFFFFF）；亮背景 → 深灰字（#1F1F1F，比纯黑更护眼）
            // 中间色（0.179 附近）再用 YIQ 微调：若背景偏暖(橙/红/黄)选深青字，偏冷(蓝/绿)选深灰字
            resDic["Color.ListItem.Selected.Fore"] = maxRelativeLuminance < 0.179
                ? Colors.White
                : PickLightForeground(candidates, maxRelativeLuminance);
        }

        /// <summary>
        /// WCAG 2.1 相对亮度计算（sRGB → 线性光 → 加权）
        /// https://www.w3.org/TR/WCAG21/#dfn-relative-luminance
        /// </summary>
        private static double GetRelativeLuminance(Color c)
        {
            double r = SrgbToLinear(c.R / 255.0);
            double g = SrgbToLinear(c.G / 255.0);
            double b = SrgbToLinear(c.B / 255.0);
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        private static double SrgbToLinear(double channel)
        {
            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// 当背景偏亮时，用 YIQ 色相判断该用暖色调还是冷色调前景，
        /// 比"一律黑色"在暖底（橙/粉/红）上对比度更好。
        /// </summary>
        private static Color PickLightForeground(List<Color> candidates, double lum)
        {
            // 取候选色中最有代表性的（亮度最高的）
            Color bg = candidates.OrderByDescending(GetRelativeLuminance).First();

            // YIQ: Y 是亮度，I 是橙/蓝温差，Q 是紫/绿温差
            double yiq = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0
                       + (0.596 * (bg.R - bg.G) + 0.321 * (bg.R - bg.B)) / 255.0;

            // I > 0 偏暖（红/橙/黄/粉）→ 用深青色前景；I < 0 偏冷 → 用深灰前景
            // 这样在任何彩色背景上对比度都比纯黑好
            return yiq > 0.5
                ? Color.FromArgb(0xFF, 0x0B, 0x3A, 0x66)  // 深蓝 #0B3A66
                : Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F); // 深灰 #1F1F1F（与 FG1 默认值一致）
        }

        private static Color? ResolveColor(Application app, ResourceDictionary resDic, string key)
        {
            if (resDic.ContainsKey(key) && resDic[key] is Color c1) return c1;
            if (app.TryFindResource(key, app.ActualThemeVariant, out var found) && found is Color c2) return c2;
            if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var rv) && rv is Color c3) return c3;
            return null;
        }

        private static Avalonia.Animation.Easings.IEasing CreateEasingByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var candidates = new string[] {
                name.Trim(),
                name.Trim() + "Ease",       // 允许写 "CubicEaseOut" 或 "CubicEaseOutEase"（后者虽冗余也兼容）
                name.Trim().EndsWith("Ease", StringComparison.OrdinalIgnoreCase) ? name.Trim().Substring(0, name.Trim().Length - 4) : name.Trim(),
            };

            foreach (var c in candidates)
            {
                // 先在 Avalonia.Animation.Easings 命名空间里找（大小写不敏感）
                var t = Type.GetType("Avalonia.Animation.Easings." + c + ", Avalonia.Base", false, true);
                if (t != null && typeof(Avalonia.Animation.Easings.IEasing).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null)
                {
                    try { return (Avalonia.Animation.Easings.IEasing)Activator.CreateInstance(t); } catch { /* ignore */ }
                }
            }
            return null;
        }

        public static void SetFonts(string defaultFont, string monospaceFont)
        {
            if (Current is not App app)
                return;

            if (app._fontsOverrides != null)
            {
                app.Resources.MergedDictionaries.Remove(app._fontsOverrides);
                app._fontsOverrides = null;
            }

            defaultFont = StringExtensions.FormatFontNames(defaultFont);
            monospaceFont = StringExtensions.FormatFontNames(monospaceFont);

            var resDic = new ResourceDictionary();
            if (!string.IsNullOrEmpty(defaultFont))
                resDic.Add("Fonts.Default", new FontFamily(defaultFont));

            if (string.IsNullOrEmpty(monospaceFont))
            {
                if (!string.IsNullOrEmpty(defaultFont))
                {
                    monospaceFont = $"fonts:SourceGit#JetBrains Mono NL,{defaultFont}";
                    resDic.Add("Fonts.Monospace", FontFamily.Parse(monospaceFont));
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(defaultFont) && !monospaceFont.Contains(defaultFont, StringComparison.Ordinal))
                    monospaceFont = $"{monospaceFont},{defaultFont}";

                resDic.Add("Fonts.Monospace", FontFamily.Parse(monospaceFont));
            }

            if (resDic.Count > 0)
            {
                app.Resources.MergedDictionaries.Add(resDic);
                app._fontsOverrides = resDic;
            }
        }

        public static string Text(string key, params object[] args)
        {
            var fmt = Current?.FindResource($"Text.{key}") as string;
            if (string.IsNullOrWhiteSpace(fmt))
                return $"Text.{key}";

            if (args == null || args.Length == 0)
                return fmt;

            return string.Format(fmt, args);
        }

        public static ViewModels.Launcher GetLauncher()
        {
            return Current is App app ? app._launcher : null;
        }

        public static void Quit(int exitCode)
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(exitCode);
            else
                Environment.Exit(exitCode);
        }
        #endregion

        #region Overrides
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            var pref = ViewModels.Preferences.Instance;
            SetLocale(pref.Locale);
            SetTheme(pref.Theme, pref.ThemeOverrides);
            SetFonts(pref.DefaultFontFamily, pref.MonospaceFontFamily);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                BindingPlugins.DataValidators.RemoveAt(0);

                // Disable tooltip if window is not active.
                ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((c, e) =>
                {
                    var topLevel = TopLevel.GetTopLevel(c);
                    if (topLevel is not Window { IsActive: true })
                        e.Cancel = true;
                });

                if (TryLaunchAsFileHistoryViewer(desktop))
                    return;

                if (TryLaunchAsBlameViewer(desktop))
                    return;

                if (TryLaunchAsCoreEditor(desktop))
                    return;

                if (TryLaunchAsAskpass(desktop))
                    return;

                TryLaunchAsNormal(desktop);
            }
        }
        #endregion

        #region Launch Ways
        private static bool TryLaunchAsRebaseTodoEditor(string[] args, out int exitCode)
        {
            exitCode = -1;

            if (args.Length <= 1 || !args[0].Equals("--rebase-todo-editor", StringComparison.Ordinal))
                return false;

            var file = args[1].Replace('\\', '/').Trim('\"').Trim();
            var filename = Path.GetFileName(file);
            if (!filename.Equals("git-rebase-todo", StringComparison.OrdinalIgnoreCase))
                return true;

            var dirInfo = new DirectoryInfo(Path.GetDirectoryName(file)!);
            if (!dirInfo.Exists || !dirInfo.Name.Equals("rebase-merge", StringComparison.Ordinal))
                return true;

            var jobsFile = Path.Combine(dirInfo.Parent!.FullName, "sourcegit.interactive_rebase");
            if (!File.Exists(jobsFile))
                return true;

            using var stream = File.OpenRead(jobsFile);
            var collection = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.InteractiveRebaseJobCollection);
            collection.WriteTodoList(file);
            exitCode = 0;
            return true;
        }

        private static bool TryLaunchAsRebaseMessageEditor(string[] args, out int exitCode)
        {
            exitCode = -1;

            if (args.Length <= 1 || !args[0].Equals("--rebase-message-editor", StringComparison.Ordinal))
                return false;

            exitCode = 0;

            var file = args[1].Replace('\\', '/').Trim('\"').Trim();
            var filename = Path.GetFileName(file);
            if (!filename.Equals("COMMIT_EDITMSG", StringComparison.OrdinalIgnoreCase))
                return true;

            var gitDir = Path.GetDirectoryName(file)!;
            var origHeadFile = Path.Combine(gitDir, "rebase-merge", "orig-head");
            var ontoFile = Path.Combine(gitDir, "rebase-merge", "onto");
            var doneFile = Path.Combine(gitDir, "rebase-merge", "done");
            var jobsFile = Path.Combine(gitDir, "sourcegit.interactive_rebase");
            if (!File.Exists(ontoFile) || !File.Exists(origHeadFile) || !File.Exists(doneFile) || !File.Exists(jobsFile))
                return true;

            var origHead = File.ReadAllText(origHeadFile).Trim();
            var onto = File.ReadAllText(ontoFile).Trim();
            using var stream = File.OpenRead(jobsFile);
            var collection = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.InteractiveRebaseJobCollection);
            if (collection.Onto.StartsWith(onto, StringComparison.OrdinalIgnoreCase) && collection.OrigHead.StartsWith(origHead, StringComparison.OrdinalIgnoreCase))
                collection.WriteCommitMessage(doneFile, file);

            return true;
        }

        private bool TryLaunchAsFileHistoryViewer(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args;
            if (args is not { Length: > 1 } || !args[0].Equals("--history", StringComparison.Ordinal))
                return false;

            var fullPath = Path.GetFullPath(args[1].Replace('\\', '/').Trim('\"').Trim());
            var dir = Path.GetDirectoryName(fullPath);

            var test = new Commands.QueryRepositoryRootPath(dir).GetResult();
            if (!test.IsSuccess || string.IsNullOrEmpty(test.StdOut))
            {
                Console.Out.WriteLine($"'{args[1]}' is not in a valid git repository");
                desktop.Shutdown(-1);
                return true;
            }

            Native.OS.SetupExternalTools();
            Models.AvatarManager.Instance.Start();

            var repo = test.StdOut.Trim();
            var relativePath = Path.GetRelativePath(repo, fullPath).Replace('\\', '/');
            if (File.Exists(fullPath))
            {
                desktop.MainWindow = new Views.FileHistories()
                {
                    DataContext = new ViewModels.FileHistories(repo, relativePath)
                };
            }
            else if (Directory.Exists(fullPath))
            {
                desktop.MainWindow = new Views.DirHistories()
                {
                    DataContext = new ViewModels.DirHistories(repo, relativePath.TrimEnd('/'))
                };
            }
            else
            {
                Console.Out.WriteLine($"'{args[1]}' does not exist in repository: '{repo}'");
                desktop.Shutdown(-1);
            }

            return true;
        }

        private bool TryLaunchAsBlameViewer(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args;
            if (args is not { Length: > 1 } || !args[0].Equals("--blame", StringComparison.Ordinal))
                return false;

            var file = Path.GetFullPath(args[1].Replace('\\', '/').Trim('\"').Trim());
            var dir = Path.GetDirectoryName(file);

            var test = new Commands.QueryRepositoryRootPath(dir).GetResult();
            if (!test.IsSuccess || string.IsNullOrEmpty(test.StdOut))
            {
                Console.Out.WriteLine($"'{args[1]}' is not in a valid git repository");
                desktop.Shutdown(-1);
                return true;
            }

            var repo = test.StdOut.Trim();
            var head = new Commands.QuerySingleCommit(repo, "HEAD").GetResult();
            if (head == null)
            {
                Console.Out.WriteLine($"{repo} has no commits!");
                desktop.Shutdown(-1);
                return true;
            }

            var relFile = Path.GetRelativePath(repo, file);
            var viewer = new Views.Blame()
            {
                DataContext = new ViewModels.Blame(repo, relFile, head)
            };
            desktop.MainWindow = viewer;
            return true;
        }

        private bool TryLaunchAsCoreEditor(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args;
            if (args is not { Length: > 1 } || !args[0].Equals("--core-editor", StringComparison.Ordinal))
                return false;

            var file = args[1].Replace('\\', '/').Trim('\"').Trim();
            if (!File.Exists(file))
            {
                desktop.Shutdown(-1);
                return true;
            }

            var editor = new Views.CommitMessageEditor();
            editor.AsStandalone(file);
            desktop.MainWindow = editor;
            return true;
        }

        private bool TryLaunchAsAskpass(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var launchAsAskpass = Environment.GetEnvironmentVariable("SOURCEGIT_LAUNCH_AS_ASKPASS");
            if (launchAsAskpass is not "TRUE")
                return false;

            var args = desktop.Args;
            if (args?.Length > 0)
            {
                var askpass = new Views.Askpass();
                askpass.TxtDescription.Text = args[0];
                desktop.MainWindow = askpass;
                return true;
            }

            return false;
        }

        private void TryLaunchAsNormal(IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ipcChannel = new Models.IpcChannel();
            if (!_ipcChannel.IsFirstInstance)
            {
                var arg = desktop.Args is { Length: > 0 } ? desktop.Args[0] : string.Empty;
                if (!string.IsNullOrEmpty(arg))
                {
                    arg = arg.Replace('\\', '/').TrimEnd('/').Trim('\"').Trim();
                    if (arg.Length > 0 && !Path.IsPathFullyQualified(arg))
                        arg = Path.GetFullPath(arg);
                }

                _ipcChannel.SendToFirstInstance(arg);
                Environment.Exit(0);
                return;
            }

            Native.OS.SetupExternalTools();
            Models.AvatarManager.Instance.Start();

            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs { Files: { Count: > 0 } } fileArgs)
                        _launcher?.TryOpenRepositoryFromPath(fileArgs.Files[0].Path.LocalPath);
                };
            }

            string startupRepo = null;
            if (desktop.Args is { Length: 1 })
            {
                var arg = desktop.Args[0].Replace('\\', '/').TrimEnd('/').Trim('\"').Trim();
                if (Directory.Exists(arg))
                    startupRepo = arg;
            }

            var pref = ViewModels.Preferences.Instance;
            pref.SetCanModify();
            pref.UpdateAvailableAIModels();

            _launcher = new ViewModels.Launcher(startupRepo);
            desktop.MainWindow = new Views.Launcher() { DataContext = _launcher };
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Fix macOS crash when quiting from Dock
            if (OperatingSystem.IsMacOS())
            {
                desktop.ShutdownRequested += (_, e) =>
                {
                    e.Cancel = true;
                    Dispatcher.UIThread.Post(() => Quit(0));
                };
            }

            desktop.Exit += (_, _) =>
            {
                _ipcChannel?.Dispose();
                _ipcChannel = null;
            };

            _ipcChannel.MessageReceived += repo =>
            {
                Dispatcher.UIThread.Invoke(() =>
                {
                    _launcher.TryOpenRepositoryFromPath(repo);
                    if (desktop.MainWindow is Views.Launcher main)
                        main.BringToTop();
                });
            };

#if !DISABLE_UPDATE_DETECTION
            if (pref.ShouldCheck4UpdateOnStartup())
                Check4Update();
#endif
        }
        #endregion

        #region Check for Updates
        private void Check4Update(bool manually = false)
        {
            if (_launcher != null)
                _launcher.NewVersion = null;

            Task.Run(async () =>
            {
                try
                {
                    // Fetch latest release information.
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(5);

                    var data = await client.GetStringAsync("https://sourcegit-scm.github.io/data/version.json");
                    var ver = JsonSerializer.Deserialize(data, JsonCodeGen.Default.Version);
                    if (ver == null)
                        return;

                    if (manually)
                    {
                        if (ver.IsNewVersion)
                            ShowSelfUpdateResult(ver);
                        else
                            ShowSelfUpdateResult(new Models.AlreadyUpToDate());
                    }
                    else if (_launcher != null)
                    {
                        if (!ver.IsNewVersion || ver.TagName == ViewModels.Preferences.Instance.IgnoreUpdateTag)
                            return;

                        _launcher.NewVersion = ver;
                    }
                }
                catch (Exception e)
                {
                    if (manually)
                        ShowSelfUpdateResult(new Models.SelfUpdateFailed(e));
                }
            });
        }

        private void ShowSelfUpdateResult(object data)
        {
            try
            {
                Dispatcher.UIThread.Invoke(async () =>
                {
                    if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                    {
                        var ctx = new ViewModels.SelfUpdate { Data = data };
                        var dialog = new Views.SelfUpdate() { DataContext = ctx };
                        await dialog.ShowDialog(owner);
                    }
                });
            }
            catch
            {
                // Ignore exceptions.
            }
        }
        #endregion

        private Models.IpcChannel _ipcChannel = null;
        private ViewModels.Launcher _launcher = null;
        private ResourceDictionary _activeLocale = null;
        private ResourceDictionary _themeOverrides = null;
        private ResourceDictionary _fontsOverrides = null;
    }
}
