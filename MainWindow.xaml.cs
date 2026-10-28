using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RAM.Controls;
using RAM.Core;
using Forms = System.Windows.Forms;

namespace RAM;

public enum ToastKind { Info, Success, Error }

public enum StepState { Pending, Running, Done, Failed }

public sealed class LaunchStep : ObservableObject
{
    string _status = "Na fila";
    StepState _state;

    public required Account Account { get; init; }
    public string Title => Account.Title;
    public string Status { get => _status; set => Set(ref _status, value); }
    public StepState State { get => _state; set => Set(ref _state, value); }
}

public partial class MainWindow : Window
{
    static readonly string[] Verbs =
    {
        "Clawdando", "Teleportando", "Spawnando", "Carregando o avatar", "Pulando obbies",
        "Empilhando blocos", "Equipando acessórios", "Calibrando o pulo", "Desviando de lava",
    };

    readonly ObservableCollection<LaunchStep> _steps = new();
    readonly ICollectionView _accountsView;
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _toastTimer = new();
    readonly DispatcherTimer _verbTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(4) };
    CancellationTokenSource? _previewCts;
    GameInfo? _previewGame;
    Account? _editing;
    Forms.NotifyIcon? _tray;
    Forms.ToolStripMenuItem? _trayMulti;
    string _launchSummary = "";
    int _verbIndex;
    bool _launching;
    bool _syncingSettings;
    bool _removeArmed;
    bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width - 32);
        Height = Math.Min(Height, workArea.Height - 32);
        MinHeight = Math.Min(MinHeight, Height);

        _accountsView = CollectionViewSource.GetDefaultView(App.Store.Accounts);
        _accountsView.Filter = FilterAccount;
        AccountList.ItemsSource = _accountsView;
        SelectedChips.ItemsSource = AccountList.SelectedItems;
        InstanceList.ItemsSource = App.Tracker.Instances;
        LaunchSteps.ItemsSource = _steps;
        RecentList.ItemsSource = App.Settings.RecentPlaces;

        _previewTimer.Tick += async (_, _) =>
        {
            _previewTimer.Stop();
            await UpdatePreviewAsync();
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };
        _verbTimer.Tick += (_, _) => RotateVerb();

        App.Store.Accounts.CollectionChanged += (_, _) => UpdateHome();
        App.Multi.StateChanged += (_, _) => UpdateMultiUi();
        App.Tracker.Changed += (_, _) => UpdateInstancesUi();
        App.Tracker.AccountStarted += (_, account) =>
        {
            foreach (var step in _steps.Where(s => s.Account == account && s.State == StepState.Done))
                step.Status = "Aberto";
        };
        App.ThemeChanged += (_, _) => UpdateThemeUi();
        // Progress callbacks arrive on download threads.
        App.Updater.Changed += (_, _) => Dispatcher.BeginInvoke(UpdateUpdaterUi);
        _updateTimer.Tick += (_, _) => _ = App.Updater.CheckAndDownloadAsync();

        SourceInitialized += (_, _) =>
        {
            // Win11 rounded corners for a chromeless window (same trick as MultiBloxy's DwmHelper).
            var hwnd = new WindowInteropHelper(this).Handle;
            int preference = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };
        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            Root.Margin = max ? new Thickness(7) : new Thickness(0);
            MaxBtn.Content = max ? "" : "";
        };
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutText.Text = $"R.A.M {version?.ToString(3)}  ·  multi-instância reconstruída a partir do MultiBloxy";

        LoadSettingsUi();
        UpdateThemeUi();
        ShowPage("home");
        UpdateHome();
        UpdateMultiUi();
        UpdateInstancesUi();
        SetupTray();
        App.Tracker.Start();

        if (App.Store.LoadWarning != null) ShowToast(App.Store.LoadWarning, ToastKind.Error);
        if (App.Store.Accounts.Count > 0) AccountList.SelectedIndex = 0;
        if (App.Settings.MultiInstanceOnStartup) await EnableMultiAsync(quiet: true);
        UpdateUpdaterUi();
        _updateTimer.Start();
        _ = App.Updater.CheckAndDownloadAsync();
        await RefreshAvatarsAsync();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible)
        {
            CloseDialog();
            e.Handled = true;
        }
        else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenAddDialog();
            e.Handled = true;
        }
    }

    // ───────────────────────── Navigation ─────────────────────────

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ShowPage((string)((FrameworkElement)sender).Tag);
    }

    void ShowPage(string page)
    {
        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        InstancesPage.Visibility = page == "instances" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "instances" => "Instâncias", "settings" => "Configurações", _ => "" };
        if (page == "settings") UpdateLaunchPathText();
    }

    void GoHome() => NavHome.IsChecked = true;

    // ───────────────────────── Accounts list ─────────────────────────

    bool FilterAccount(object item)
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0 || item is not Account a) return true;
        return a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               a.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (a.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => _accountsView.Refresh();

    void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionUi();
        UpdateGreeting();
    }

    void AccountList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Account account) OpenEditDialog(account);
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.Items.Count > 0 && AccountList.SelectedItems.Count == AccountList.Items.Count) AccountList.UnselectAll();
        else AccountList.SelectAll();
    }

    void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Account account) return;
        GoHome();
        _ = LaunchAsync(new[] { account }, null);
    }

    void EditAccount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Account account) OpenEditDialog(account);
    }

    async Task RefreshAvatarsAsync()
    {
        if (App.Store.Accounts.Count == 0) return;
        try
        {
            var avatars = await App.Api.GetHeadshotsAsync(App.Store.Accounts.Select(a => a.UserId));
            foreach (var account in App.Store.Accounts)
                if (avatars.TryGetValue(account.UserId, out var url)) account.AvatarUrl = url;
            App.Store.Save();
        }
        catch (Exception ex)
        {
            Log.Error("refresh avatars", ex);
        }
    }

    // ───────────────────────── Home / composer ─────────────────────────

    void UpdateHome()
    {
        int count = App.Store.Accounts.Count;
        bool any = count > 0;
        Composer.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ComposerHint.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyAddBtn.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SidebarEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        AccountsHeader.Text = any ? $"Contas · {count}" : "Contas";
        SelectAllBtn.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGreeting();
        UpdateSelectionUi();
    }

    void UpdateGreeting()
    {
        int hour = DateTime.Now.Hour;
        string hello = hour < 5 ? "Boa madrugada" : hour < 12 ? "Bom dia" : hour < 18 ? "Boa tarde" : "Boa noite";
        int selected = AccountList.SelectedItems.Count;
        Greeting.Text = selected switch
        {
            1 => $"{hello}, {((Account)AccountList.SelectedItems[0]!).Title}",
            > 1 => $"{hello}, esquadrão de {selected}",
            _ => hello,
        };
        SubGreeting.Text = App.Store.Accounts.Count == 0
            ? "Abra várias janelas do Roblox ao mesmo tempo e entre em cada uma pela tela do próprio Roblox."
            : "Para onde vamos hoje?";
    }

    void UpdateSelectionUi()
    {
        int n = AccountList.SelectedItems.Count;
        SelectionText.Text = n switch { 0 => "Nenhuma conta", 1 => "1 conta", _ => $"{n} contas" };
        SelectAllBtn.Content = n > 0 && n == AccountList.Items.Count ? "Limpar seleção" : "Selecionar todas";
        SelectedChips.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        // An empty box is valid: the arrow then opens Roblox on the home screen, just like "Abrir sem jogo".
        bool empty = string.IsNullOrWhiteSpace(PlaceInput.Text);
        SendBtn.IsEnabled = n > 0 && !_launching && (empty || InputParser.Parse(PlaceInput.Text) != null);
        SendBtn.ToolTip = empty ? "Abrir o Roblox na tela inicial (Enter)" : "Entrar no jogo (Enter)";
        LaunchHomeBtn.IsEnabled = n > 0 && !_launching;
    }

    void PlaceInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSelectionUi();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    void PlaceInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (SendBtn.IsEnabled) _ = LaunchSelectedAsync(withTarget: !string.IsNullOrWhiteSpace(PlaceInput.Text));
    }

    void JobToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = JobRow.Visibility != Visibility.Visible;
        JobRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) JobInput.Focus();
        else JobInput.Text = "";
    }

    void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentPlace place) return;
        PlaceInput.Text = place.PlaceId.ToString();
        PlaceInput.Focus();
        PlaceInput.CaretIndex = PlaceInput.Text.Length;
    }

    async Task UpdatePreviewAsync()
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        var target = InputParser.Parse(PlaceInput.Text, JobInput.Text);
        if (target == null)
        {
            GamePreview.Visibility = Visibility.Collapsed;
            return;
        }

        GamePreview.Visibility = Visibility.Visible;
        GameIcon.Source = null;
        if (target.NeedsResolve)
        {
            GameName.Text = target.ShareType == "Server" ? "Servidor privado (link de convite)" : "Convite para o servidor de um amigo";
            GameMeta.Text = "O jogo é descoberto quando você entrar";
            return;
        }
        if (_previewGame?.PlaceId == target.PlaceId)
        {
            ShowGame(_previewGame, target);
            return;
        }

        GameName.Text = "Procurando jogo…";
        GameMeta.Text = $"Place {target.PlaceId}";
        try
        {
            var info = await App.Api.GetGameInfoAsync(target.PlaceId, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (info == null)
            {
                GameName.Text = $"Place {target.PlaceId}";
                GameMeta.Text = "Não encontrei informações deste jogo";
                return;
            }
            _previewGame = info;
            ShowGame(info, target);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            GameName.Text = $"Place {target.PlaceId}";
            GameMeta.Text = "Sem conexão com a API do Roblox";
            Log.Error("game preview", ex);
        }
    }

    void ShowGame(GameInfo info, LaunchTarget target)
    {
        GameName.Text = info.Name;
        var bits = new List<string>();
        if (!string.IsNullOrEmpty(info.Creator)) bits.Add("por " + info.Creator);
        bits.Add($"{info.Playing:N0} jogando");
        if (target.LinkCode != null) bits.Add("servidor privado");
        else if (target.JobId != null) bits.Add("servidor específico");
        GameMeta.Text = string.Join("  ·  ", bits);
        GameIcon.Source = new UrlToImageConverter().Convert(info.IconUrl, typeof(ImageSource), null,
            System.Globalization.CultureInfo.InvariantCulture) as ImageSource;
    }

    // ───────────────────────── Launching ─────────────────────────

    void Launch_Click(object sender, RoutedEventArgs e) =>
        _ = LaunchSelectedAsync(withTarget: !string.IsNullOrWhiteSpace(PlaceInput.Text));

    void LaunchHome_Click(object sender, RoutedEventArgs e) => _ = LaunchSelectedAsync(withTarget: false);

    async Task LaunchSelectedAsync(bool withTarget)
    {
        var selected = AccountList.SelectedItems.OfType<Account>().ToHashSet();
        var accounts = App.Store.Accounts.Where(selected.Contains).ToList();
        LaunchTarget? target = null;
        if (withTarget)
        {
            target = InputParser.Parse(PlaceInput.Text, JobInput.Text);
            if (target == null)
            {
                ShowToast("Não reconheci esse Place ID ou link.", ToastKind.Error);
                return;
            }
        }
        await LaunchAsync(accounts, target);
    }

    async Task LaunchAsync(IReadOnlyList<Account> accounts, LaunchTarget? target)
    {
        if (accounts.Count == 0) return;
        if (_launching)
        {
            ShowToast("Espere as contas atuais terminarem de abrir.");
            return;
        }
        _launching = true;
        UpdateSelectionUi();

        _steps.Clear();
        foreach (var account in accounts) _steps.Add(new LaunchStep { Account = account });
        string destination = target == null ? "Roblox"
            : target.NeedsResolve ? "servidor privado"
            : _previewGame?.PlaceId == target.PlaceId ? _previewGame.Name
            : $"place {target.PlaceId}";
        _launchSummary = (accounts.Count == 1 ? accounts[0].Title : $"{accounts.Count} contas") + " → " + destination;
        ProgressCard.Visibility = Visibility.Visible;
        LaunchSpinner.Visibility = Visibility.Visible;
        _verbIndex = new Random().Next(Verbs.Length);
        RotateVerb();
        _verbTimer.Start();
        HeroClawd.Mood = ClawdMood.Working;
        BrandClawd.Mood = ClawdMood.Working;

        if ((accounts.Count > 1 || RobloxProcesses.AnyRunning()) && !App.Multi.IsActive)
        {
            ShowToast("Ativando a multi-instância para abrir mais de um Roblox…");
            await EnableMultiAsync(quiet: true);
        }

        int opened = 0;
        string? placeName = null, placeIcon = null;
        try
        {
            for (int i = 0; i < accounts.Count; i++)
            {
                var step = _steps[i];
                var account = step.Account;
                if (i > 0)
                {
                    for (int s = App.Settings.LaunchDelaySeconds; s > 0; s--)
                    {
                        step.Status = $"Aguardando {s}s…";
                        await Task.Delay(1000);
                    }
                }

                step.State = StepState.Running;
                step.Status = "Gerando ticket…";
                account.Status = "Abrindo…";
                try
                {
                    if (target is { NeedsResolve: true })
                    {
                        step.Status = "Resolvendo link…";
                        var (placeId, linkCode, jobId) = await App.Api.ResolveShareLinkAsync(account, target.ShareCode!, target.ShareType);
                        target = target with { PlaceId = placeId, LinkCode = linkCode, JobId = jobId ?? target.JobId };
                    }

                    var ticket = await App.Api.GetAuthTicketAsync(account);
                    account.IsInvalid = false;

                    // The same account in two clients gets kicked ("same account launched from another device").
                    var previous = App.Tracker.Instances.Where(x => x.Account == account).Select(x => x.Pid).ToList();
                    if (App.Settings.CloseDuplicateSessions && previous.Count > 0)
                    {
                        step.Status = "Fechando a sessão anterior desta conta…";
                        await Task.WhenAll(previous.Select(pid => RobloxProcesses.CloseAsync(pid, 3000)));
                        App.Tracker.Refresh();
                    }

                    if (App.Settings.IsolateSessions && App.Multi.IsActive && !App.Session.IsActive)
                        await ApplySessionIsolationAsync();

                    if (string.IsNullOrEmpty(account.BrowserTrackerId))
                        account.BrowserTrackerId = RobloxLauncher.NewTrackerId();

                    step.Status = "Abrindo Roblox…";
                    App.Tracker.ExpectLaunch(account, RobloxProcesses.GetPids().ToHashSet());
                    RobloxLauncher.Start(RobloxLauncher.BuildUri(ticket, target, account.BrowserTrackerId), App.Settings.DirectLaunch);
                    account.LastLaunchedAt = DateTime.Now;
                    step.State = StepState.Done;
                    step.Status = "Esperando a janela do Roblox…";
                    opened++;
                }
                catch (RobloxApiException ex)
                {
                    step.State = StepState.Failed;
                    step.Status = ex.Message;
                    if (ex.CookieInvalid) account.IsInvalid = true;
                }
                catch (HttpRequestException)
                {
                    step.State = StepState.Failed;
                    step.Status = "Sem conexão com o Roblox";
                }
                catch (TaskCanceledException)
                {
                    step.State = StepState.Failed;
                    step.Status = "O Roblox demorou demais para responder";
                }
                catch (Exception ex)
                {
                    step.State = StepState.Failed;
                    step.Status = ex.Message;
                    Log.Error("launch", ex);
                }
                finally
                {
                    account.Status = null;
                }
            }

            if (target is { PlaceId: > 0 } && opened > 0)
            {
                var info = _previewGame?.PlaceId == target.PlaceId ? _previewGame : await SafeGameInfo(target.PlaceId);
                placeName = info?.Name ?? $"Place {target.PlaceId}";
                placeIcon = info?.IconUrl;
            }
        }
        finally
        {
            _launching = false;
            _verbTimer.Stop();
            LaunchSpinner.Visibility = Visibility.Collapsed;
            bool allOk = opened == accounts.Count;
            LaunchHeadline.Text = allOk
                ? (opened == 1 ? "Pronto! O Roblox está abrindo." : $"Pronto! {opened} contas abrindo.")
                : $"{opened} de {accounts.Count} contas abertas";
            HeroClawd.Flash(allOk ? ClawdMood.Happy : ClawdMood.Error, 2.4);
            BrandClawd.Mood = ClawdMood.Idle;
            UpdateSelectionUi();
            App.Store.Save();
            if (placeName != null && target != null) App.Settings.RememberPlace(target.PlaceId, placeName, placeIcon);
            if (!allOk) ShowToast("Algumas contas não abriram — veja os detalhes no cartão.", ToastKind.Error);
        }
    }

    static async Task<GameInfo?> SafeGameInfo(long placeId)
    {
        try { return await App.Api.GetGameInfoAsync(placeId); }
        catch { return null; }
    }

    void RotateVerb()
    {
        LaunchHeadline.Text = $"{Verbs[_verbIndex++ % Verbs.Length]}…  {_launchSummary}";
    }

    void NewInstance_Click(object sender, RoutedEventArgs e) => _ = LaunchBlankAsync();

    /// <summary>Opens another Roblox window with no saved account; sign-in happens in Roblox itself.</summary>
    async Task LaunchBlankAsync()
    {
        if (!App.Multi.IsActive && RobloxProcesses.AnyRunning()) await EnableMultiAsync(quiet: true);
        try
        {
            RobloxLauncher.StartBlank(App.Settings.DirectLaunch);
            BrandClawd.Flash(ClawdMood.Happy);
            ShowToast(App.Multi.IsActive
                ? "Abrindo outra janela do Roblox — entre na conta pela tela do Roblox."
                : "Abrindo o Roblox… (multi-instância desligada: só uma janela por vez)");
        }
        catch (Exception ex)
        {
            Log.Error("blank launch", ex);
            ShowToast("Não consegui abrir o Roblox: " + ex.Message, ToastKind.Error);
        }
    }

    // ───────────────────────── Multi-instance ─────────────────────────

    async Task<bool> EnableMultiAsync(bool quiet)
    {
        MultiPill.IsEnabled = MultiSwitch.IsEnabled = false;
        MultiPillText.Text = "Ativando…";
        bool ok = await App.Multi.EnableAsync(App.Settings.HoldLegacyMutex, releaseCookieLock: false);
        if (ok) await ApplySessionIsolationAsync();
        MultiPill.IsEnabled = MultiSwitch.IsEnabled = true;
        if (ok)
        {
            var scan = App.Multi.LastScan;
            if (scan is { SingletonsClosed: > 0 })
                ShowToast($"O Roblox já estava aberto — liberei {scan.SingletonsClosed} handle(s). Multi-instância ativa.", ToastKind.Success);
            else if (!quiet)
                ShowToast("Multi-instância ativada — abra quantas contas quiser.", ToastKind.Success);
        }
        else
        {
            ShowToast("Não consegui ativar a multi-instância: " + App.Multi.LastError, ToastKind.Error);
            MultiClawd.Flash(ClawdMood.Error);
        }
        UpdateMultiUi();
        return ok;
    }

    async Task ToggleMultiAsync()
    {
        if (App.Multi.IsActive)
        {
            App.Multi.Disable();
            App.Session.Disable();
            ShowToast("Multi-instância desligada.");
        }
        else
        {
            await EnableMultiAsync(quiet: false);
        }
    }

    /// <summary>Session isolation follows the multi-instance switch and the setting.</summary>
    async Task ApplySessionIsolationAsync()
    {
        if (!App.Settings.IsolateSessions || !App.Multi.IsActive)
        {
            App.Session.Disable();
            return;
        }
        if (!await App.Session.EnableAsync())
            ShowToast("Não consegui isolar as sessões: " + App.Session.LastError, ToastKind.Error);
    }

    void UpdateMultiUi()
    {
        bool on = App.Multi.IsActive;
        MultiSwitch.IsChecked = on;
        MultiPillText.Text = on ? "Multi-instância ativa" : "Multi-instância desligada";
        MultiDot.SetResourceReference(Shape.FillProperty, on ? "Success" : "Text.Muted");
        MultiDotHalo.SetResourceReference(Shape.FillProperty, on ? "Success.Soft" : "Bg.Selected");
        MultiStatusText.Text = App.Multi.ProvidedExternally
            ? "Ativa — outro programa (outro R.A.M, MultiBloxy ou o seu bootstrapper) já está segurando o ROBLOX_singletonEvent, então a multi-instância já funciona."
            : on
            ? "Ativa — segurando ROBLOX_singletonEvent" + (App.Multi.LegacyHeld ? " e ROBLOX_singletonMutex" : "") +
              ". Cada cliente novo acha que é o único aberto." +
              (App.Session.IsActive ? "\nSessões isoladas: cada janela guarda só o próprio login." : "")
            : "Desligada — o Roblox só permite uma janela por vez." +
              (App.Multi.LastError != null ? "\nÚltimo erro: " + App.Multi.LastError : "");
        MultiClawd.Mood = on ? ClawdMood.Idle : ClawdMood.Sleeping;
        if (_trayMulti != null) _trayMulti.Checked = on;
    }

    async void MultiPill_Click(object sender, RoutedEventArgs e) => await ToggleMultiAsync();

    async void MultiSwitch_Click(object sender, RoutedEventArgs e) => await ToggleMultiAsync();

    async void ReleaseHandles_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => RobloxHandleCloser.Run(true, cookieLock: false));
            if (result.Processes == 0)
            {
                ShowToast("Nenhum Roblox aberto — nada para liberar.");
            }
            else
            {
                var msg = $"{result.SingletonsClosed} handle(s) liberado(s) em {result.Processes} processo(s) " +
                          $"({result.Elapsed.TotalMilliseconds:0} ms)";
                if (result.CookieLocksClosed > 0) msg += $", {result.CookieLocksClosed} trava(s) de cookies";
                ShowToast(msg + ".", result.Errors.Count == 0 ? ToastKind.Success : ToastKind.Error);
            }
            if (!App.Multi.IsActive) await EnableMultiAsync(quiet: true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ───────────────────────── Instances ─────────────────────────

    void UpdateInstancesUi()
    {
        int n = App.Tracker.Instances.Count;
        InstanceBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstanceBadgeText.Text = n.ToString();
        InstancesEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseAllBtn.IsEnabled = n > 0;
        InstancesSummary.Text = n switch
        {
            0 => "Nenhuma instância aberta",
            1 => "1 instância aberta",
            _ => $"{n} instâncias abertas ao mesmo tempo",
        };
        if (_tray != null) _tray.Text = n == 0 ? "R.A.M" : $"R.A.M — {n} Roblox aberto(s)";
    }

    void FocusInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RobloxInstance instance) InstanceTracker.Focus(instance.Pid);
    }

    async void CloseInstance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RobloxInstance instance) return;
        await RobloxProcesses.CloseAsync(instance.Pid);
        App.Tracker.Refresh();
    }

    async void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        CloseAllBtn.IsEnabled = false;
        int n = App.Tracker.Instances.Count;
        await RobloxProcesses.CloseAllAsync();
        App.Tracker.Refresh();
        ShowToast(n == 1 ? "Instância fechada." : $"{n} instâncias fechadas.", ToastKind.Success);
    }

    // ───────────────────────── Dialogs ─────────────────────────

    void ShowDialog(FrameworkElement dialog)
    {
        AddDialog.Visibility = Visibility.Collapsed;
        EditDialog.Visibility = Visibility.Collapsed;
        dialog.Visibility = Visibility.Visible;
        Overlay.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        var scale = new ScaleTransform(0.97, 0.97);
        dialog.RenderTransformOrigin = new Point(0.5, 0.5);
        dialog.RenderTransform = scale;
        var grow = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    void CloseDialog()
    {
        Overlay.Visibility = Visibility.Collapsed;
        _editing = null;
    }

    void CloseDialog_Click(object sender, RoutedEventArgs e) => CloseDialog();

    void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddConfirmBtn.IsEnabled) return; // busy validating
        CloseDialog();
    }

    void AddAccount_Click(object sender, RoutedEventArgs e) => OpenAddDialog();

    void OpenAddDialog()
    {
        CookieInput.Text = "";
        AliasInput.Text = "";
        AddErrorBox.Visibility = Visibility.Collapsed;
        CookieHelp.Visibility = Visibility.Collapsed;
        AddClawd.Mood = ClawdMood.Idle;
        ShowDialog(AddDialog);
        Dispatcher.BeginInvoke(() => CookieInput.Focus(), DispatcherPriority.Input);
    }

    void CookieHelp_Click(object sender, RoutedEventArgs e) =>
        CookieHelp.Visibility = CookieHelp.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    void SetAddBusy(bool busy)
    {
        AddConfirmBtn.IsEnabled = !busy;
        AddSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AddConfirmText.Text = busy ? "Verificando…" : "Adicionar";
        AddClawd.Mood = busy ? ClawdMood.Working : ClawdMood.Idle;
    }

    async void AddConfirm_Click(object sender, RoutedEventArgs e)
    {
        var cookies = CookieInput.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(RobloxApi.NormalizeCookie)
            .Where(c => c.Length >= 50)
            .Distinct()
            .ToList();
        if (cookies.Count == 0)
        {
            ShowAddError("Não encontrei nenhum cookie .ROBLOSECURITY no texto colado.");
            return;
        }

        AddErrorBox.Visibility = Visibility.Collapsed;
        SetAddBusy(true);
        var errors = new List<string>();
        var failed = new List<string>();
        var added = new List<Account>();
        int updated = 0;

        for (int i = 0; i < cookies.Count; i++)
        {
            var cookie = cookies[i];
            string label = cookies.Count == 1 ? "Cookie" : $"Cookie {i + 1}";
            try
            {
                var user = await App.Api.GetAuthenticatedUserAsync(cookie);
                if (user == null)
                {
                    errors.Add($"{label}: inválido ou expirado.");
                    failed.Add(cookie);
                    continue;
                }
                var existing = App.Store.FindByUserId(user.Id);
                if (existing != null)
                {
                    existing.Cookie = cookie;
                    existing.Username = user.Name;
                    existing.DisplayName = user.DisplayName;
                    existing.IsInvalid = false;
                    updated++;
                    continue;
                }
                var account = new Account { UserId = user.Id, Cookie = cookie, Username = user.Name, DisplayName = user.DisplayName };
                if (cookies.Count == 1) account.Alias = AliasInput.Text;
                App.Store.Accounts.Add(account);
                added.Add(account);
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {(ex is HttpRequestException ? "sem conexão com o Roblox" : ex.Message)}");
                failed.Add(cookie);
            }
        }

        if (added.Count > 0)
        {
            try
            {
                var avatars = await App.Api.GetHeadshotsAsync(added.Select(a => a.UserId));
                foreach (var a in added)
                    if (avatars.TryGetValue(a.UserId, out var url)) a.AvatarUrl = url;
            }
            catch (Exception ex) { Log.Error("headshots", ex); }
        }
        App.Store.Save();
        SetAddBusy(false);

        string summary = string.Join(", ", new[]
        {
            added.Count > 0 ? (added.Count == 1 ? $"{added[0].Title} adicionada" : $"{added.Count} contas adicionadas") : null,
            updated > 0 ? $"{updated} atualizada(s)" : null,
        }.Where(s => s != null));

        if (added.Count > 0)
        {
            // A search filter could hide the new accounts, and selecting a filtered-out item throws.
            SearchBox.Text = "";
            AccountList.UnselectAll();
            foreach (var a in added) AccountList.SelectedItems.Add(a);
        }

        if (errors.Count == 0)
        {
            CloseDialog();
            ShowToast(summary.Length > 0 ? summary + "." : "Pronto.", ToastKind.Success);
            HeroClawd.Flash(ClawdMood.Happy);
        }
        else
        {
            // Keep only the cookies that failed so the user can fix them.
            CookieInput.Text = string.Join(Environment.NewLine, failed);
            ShowAddError((summary.Length > 0 ? summary + ". " : "") + string.Join(" ", errors));
            AddClawd.Flash(ClawdMood.Error);
        }
    }

    void ShowAddError(string message)
    {
        AddError.Text = message;
        AddErrorBox.Visibility = Visibility.Visible;
    }

    void OpenEditDialog(Account account)
    {
        _editing = account;
        _removeArmed = false;
        RemoveText.Text = "Remover conta";
        EditAvatar.Content = account;
        EditAlias.Text = account.Alias ?? "";
        EditNotes.Text = account.Notes ?? "";
        UpdateEditHeader();
        ShowDialog(EditDialog);
    }

    void UpdateEditHeader()
    {
        if (_editing is not { } a) return;
        EditTitle.Text = a.Title;
        var parts = new List<string> { "@" + a.Username, "ID " + a.UserId };
        if (a.LastLaunchedAt is { } last) parts.Add("aberta " + Ago(last));
        if (a.IsInvalid) parts.Add("cookie expirado");
        EditSubtitle.Text = string.Join("  ·  ", parts);
    }

    static string Ago(DateTime when)
    {
        var d = DateTime.Now - when;
        if (d.TotalMinutes < 1) return "agora";
        if (d.TotalHours < 1) return $"há {(int)d.TotalMinutes} min";
        if (d.TotalDays < 1) return $"há {(int)d.TotalHours} h";
        return $"há {(int)d.TotalDays} dia(s)";
    }

    void EditSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        _editing.Alias = EditAlias.Text;
        _editing.Notes = EditNotes.Text;
        App.Store.Save();
        CloseDialog();
        UpdateGreeting();
        ShowToast("Conta salva.", ToastKind.Success);
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        if (!_removeArmed)
        {
            _removeArmed = true;
            RemoveText.Text = "Clique de novo para confirmar";
            return;
        }
        var title = _editing.Title;
        App.Store.Accounts.Remove(_editing);
        App.Store.Save();
        CloseDialog();
        ShowToast($"{title} removida.");
    }

    void EditLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } account) return;
        CloseDialog();
        GoHome();
        _ = LaunchAsync(new[] { account }, null);
    }

    async void EditRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } account) return;
        EditRefreshBtn.IsEnabled = false;
        try
        {
            var user = await App.Api.GetAuthenticatedUserAsync(account.Cookie);
            if (user == null)
            {
                account.IsInvalid = true;
                ShowToast("O cookie desta conta expirou. Adicione a conta de novo com um cookie novo.", ToastKind.Error);
            }
            else
            {
                account.IsInvalid = false;
                account.Username = user.Name;
                account.DisplayName = user.DisplayName;
                var avatars = await App.Api.GetHeadshotsAsync(new[] { user.Id });
                if (avatars.TryGetValue(user.Id, out var url)) account.AvatarUrl = url;
                App.Store.Save();
                ShowToast("Conta verificada — o cookie está válido.", ToastKind.Success);
            }
            UpdateEditHeader();
        }
        catch (Exception ex)
        {
            ShowToast("Não foi possível verificar: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            EditRefreshBtn.IsEnabled = true;
        }
    }

    void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is not { } account) return;
        Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{account.UserId}/profile") { UseShellExecute = true })?.Dispose();
    }

    // ───────────────────────── Settings ─────────────────────────

    void LoadSettingsUi()
    {
        _syncingSettings = true;
        var s = App.Settings;
        SetMultiStartup.IsChecked = s.MultiInstanceOnStartup;
        SetLegacyMutex.IsChecked = s.HoldLegacyMutex;
        SetIsolate.IsChecked = s.IsolateSessions;
        SetCloseDuplicates.IsChecked = s.CloseDuplicateSessions;
        SetRename.IsChecked = s.RenameWindows;
        SetTray.IsChecked = s.MinimizeToTray;
        (s.DirectLaunch ? SetLaunchDirect : SetLaunchProtocol).IsChecked = true;
        SetDelay.Value = Math.Clamp(s.LaunchDelaySeconds, 0, 30);
        DelayText.Text = DelayLabel(s.LaunchDelaySeconds);
        (s.Theme switch { "light" => SetThemeLight, "dark" => SetThemeDark, _ => SetThemeSystem }).IsChecked = true;
        _syncingSettings = false;
        UpdateLaunchPathText();
    }

    static string DelayLabel(int seconds) => seconds == 0 ? "sem espera" : $"{seconds} s";

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings || !IsLoaded) return;
        var s = App.Settings;
        bool legacyBefore = s.HoldLegacyMutex;
        s.MultiInstanceOnStartup = SetMultiStartup.IsChecked == true;
        s.HoldLegacyMutex = SetLegacyMutex.IsChecked == true;
        bool isolateBefore = s.IsolateSessions;
        s.IsolateSessions = SetIsolate.IsChecked == true;
        s.CloseDuplicateSessions = SetCloseDuplicates.IsChecked == true;
        s.RenameWindows = SetRename.IsChecked == true;
        s.MinimizeToTray = SetTray.IsChecked == true;
        s.DirectLaunch = SetLaunchDirect.IsChecked == true;
        s.Save();
        UpdateLaunchPathText();

        if (legacyBefore != s.HoldLegacyMutex && App.Multi.IsActive)
        {
            App.Multi.Disable();
            _ = EnableMultiAsync(quiet: true);
        }
        else if (isolateBefore != s.IsolateSessions)
        {
            _ = ApplySessionIsolationAsync().ContinueWith(_ => Dispatcher.BeginInvoke(UpdateMultiUi));
        }
    }

    void Delay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DelayText == null) return;
        int seconds = (int)Math.Round(e.NewValue);
        DelayText.Text = DelayLabel(seconds);
        if (_syncingSettings || !IsLoaded) return;
        App.Settings.LaunchDelaySeconds = seconds;
        App.Settings.Save();
    }

    void UpdateLaunchPathText()
    {
        if (App.Settings.DirectLaunch)
        {
            var exe = RobloxLauncher.FindPlayerExe();
            LaunchPathText.Text = exe != null ? "→ " + exe : "RobloxPlayerBeta.exe não encontrado — o protocolo será usado.";
        }
        else
        {
            var handler = RobloxLauncher.GetProtocolHandler();
            LaunchPathText.Text = handler != null ? "roblox-player:// → " + handler : "Nenhum handler registrado — instale o Roblox.";
        }
        LaunchPathText.ToolTip = LaunchPathText.Text;
    }

    void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings || !IsLoaded) return;
        var theme = (string)((FrameworkElement)sender).Tag;
        App.Settings.Theme = theme;
        App.Settings.Save();
        App.ApplyTheme(theme);
    }

    void ThemeToggle_Click(object sender, RoutedEventArgs e) =>
        (App.IsDark ? SetThemeLight : SetThemeDark).IsChecked = true;

    void UpdateThemeUi() => ThemeBtn.Content = App.IsDark ? "" : "";

    void OpenDataFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true })?.Dispose();

    // ───────────────────────── Updates ─────────────────────────

    void UpdateUpdaterUi()
    {
        var u = App.Updater;
        UpdateVersionText.Text = "R.A.M " + u.CurrentVersion;
        UpdateStatusText.Text = u.State switch
        {
            UpdateState.Checking => "Procurando versão nova no GitHub…",
            UpdateState.Downloading => $"Baixando a versão nova… {u.Progress}%",
            UpdateState.Ready => $"A versão {u.ReadyVersion} foi baixada. Reinicie para instalar.",
            UpdateState.UpToDate => "Você está na versão mais recente.",
            UpdateState.Failed => "Não foi possível verificar: " + u.Error,
            UpdateState.NotInstalled => "Atualizações automáticas só funcionam na versão instalada pelo instalador.",
            _ => "O R.A.M procura atualizações no GitHub ao abrir e a cada 4 horas.",
        };
        bool busy = u.State is UpdateState.Checking or UpdateState.Downloading;
        bool ready = u.State == UpdateState.Ready;
        CheckUpdateBtn.IsEnabled = !busy && u.State != UpdateState.NotInstalled;
        CheckUpdateText.Text = ready ? "Reiniciar e atualizar" : "Verificar agora";
        UpdateBanner.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        UpdateBannerTitle.Text = $"Versão {u.ReadyVersion} pronta";
        UpdateBannerText.Text = "Reinicie para atualizar o R.A.M";
    }

    void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (App.Updater.State == UpdateState.Ready) ApplyUpdate();
        else _ = App.Updater.CheckAndDownloadAsync();
    }

    void ApplyUpdate_Click(object sender, RoutedEventArgs e) => ApplyUpdate();

    void ApplyUpdate()
    {
        // The updater ends this process without running App.OnExit, so persist and clean up first.
        App.Store.Save();
        App.Settings.Save();
        if (_tray != null) _tray.Visible = false;
        try
        {
            App.Updater.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            Log.Error("apply update", ex);
            if (_tray != null) _tray.Visible = true;
            ShowToast("Não foi possível instalar a atualização: " + ex.Message, ToastKind.Error);
        }
    }

    // ───────────────────────── Toast ─────────────────────────

    public void ShowToast(string text, ToastKind kind = ToastKind.Info)
    {
        ToastText.Text = text;
        ToastIcon.Text = kind switch { ToastKind.Success => "", ToastKind.Error => "", _ => "" };
        ToastIcon.SetResourceReference(TextBlock.ForegroundProperty,
            kind switch { ToastKind.Success => "Success", ToastKind.Error => "Danger", _ => "Accent" });

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 6 : 3.4);
        _toastTimer.Start();
    }

    void HideToast()
    {
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, TimeSpan.FromMilliseconds(220)));
    }

    // ───────────────────────── Window / tray ─────────────────────────

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void SetupTray()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ram.ico"));
            using var stream = resource!.Stream;
            _tray = new Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
                Text = "R.A.M",
                Visible = true,
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Abrir R.A.M", null, (_, _) => ShowFromTray());
            _trayMulti = new Forms.ToolStripMenuItem("Multi-instância", null, async (_, _) => await ToggleMultiAsync())
            {
                Checked = App.Multi.IsActive,
            };
            menu.Items.Add(_trayMulti);
            menu.Items.Add("Nova instância do Roblox", null, async (_, _) => await LaunchBlankAsync());
            menu.Items.Add("Fechar todas as instâncias", null, async (_, _) => await RobloxProcesses.CloseAllAsync());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Sair", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left) ShowFromTray();
            };
        }
        catch (Exception ex)
        {
            Log.Error("tray", ex);
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && App.Settings.MinimizeToTray && _tray != null)
        {
            e.Cancel = true;
            Hide();
            if (!App.Settings.TrayHintShown)
            {
                _tray.ShowBalloonTip(4000, "O R.A.M continua aberto",
                    "A multi-instância segue ativa aqui na bandeja. Clique no Clawd para voltar.", Forms.ToolTipIcon.Info);
                App.Settings.TrayHintShown = true;
                App.Settings.Save();
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
