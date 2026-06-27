using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Type4Me.Win.ASR;
using Type4Me.Win.Core;
using Type4Me.Win.Services;
using WpfControl = System.Windows.Controls.Control;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfPanel = System.Windows.Controls.Panel;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Type4Me.Win;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly Dictionary<string, WpfControl> _asrInputs = [];
    private readonly Dictionary<string, WpfControl> _llmInputs = [];
    private readonly Dictionary<Guid, HotkeyControls> _hotkeyInputs = [];
    public bool AllowClose { get; set; }
    public event Action? ModesChanged;

    public MainWindow(SettingsStore settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
        InitializeComponent();
        TrySetWindowIcon();
        Closing += (_, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            Hide();
        };
        LoadProviderCombos();
        LoadPunctuationOptions();
        RenderHotkeys();
        RefreshHistory();
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return;
            }

            Icon = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
        }
    }

    public void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void LoadProviderCombos()
    {
        AsrProviderCombo.ItemsSource = ASRProviderRegistry.All.Values.Select(d => new ComboItem<ASRProvider>(d.DisplayName, d.Provider)).ToList();
        AsrProviderCombo.SelectedValuePath = nameof(ComboItem<ASRProvider>.Value);
        AsrProviderCombo.DisplayMemberPath = nameof(ComboItem<ASRProvider>.Label);
        AsrProviderCombo.SelectedValue = _settings.SelectedASRProvider;

        LlmProviderCombo.ItemsSource = LLMProviderRegistry.All.Values.Select(d => new ComboItem<LLMProvider>(d.DisplayName, d.Provider)).ToList();
        LlmProviderCombo.SelectedValuePath = nameof(ComboItem<LLMProvider>.Value);
        LlmProviderCombo.DisplayMemberPath = nameof(ComboItem<LLMProvider>.Label);
        LlmProviderCombo.SelectedValue = _settings.SelectedLLMProvider;

        RenderAsrFields();
        RenderLlmFields();
    }

    private void LoadPunctuationOptions()
    {
        PunctuationModeCombo.ItemsSource = new[]
        {
            new ComboItem<PunctuationMode>("完整添加符号", PunctuationMode.Full),
            new ComboItem<PunctuationMode>("不添加符号", PunctuationMode.None),
            new ComboItem<PunctuationMode>("句末句号不添加，其余正常", PunctuationMode.TrimFinalPeriod)
        };
        PunctuationModeCombo.SelectedValuePath = nameof(ComboItem<PunctuationMode>.Value);
        PunctuationModeCombo.DisplayMemberPath = nameof(ComboItem<PunctuationMode>.Label);
        PunctuationModeCombo.SelectedValue = _settings.PunctuationMode;
    }

    private void AsrProviderCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AsrProviderCombo.SelectedValue is ASRProvider provider)
        {
            _settings.SelectedASRProvider = provider;
            RenderAsrFields();
        }
    }

    private void LlmProviderCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LlmProviderCombo.SelectedValue is LLMProvider provider)
        {
            _settings.SelectedLLMProvider = provider;
            RenderLlmFields();
        }
    }

    private void RenderAsrFields()
    {
        if (AsrProviderCombo.SelectedValue is ASRProvider provider)
        {
            RenderFields(AsrFieldsPanel, _asrInputs, ASRProviderRegistry.Get(provider).Fields, _settings.LoadASRCredentials(provider));
        }
    }

    private void RenderLlmFields()
    {
        if (LlmProviderCombo.SelectedValue is LLMProvider provider)
        {
            RenderFields(LlmFieldsPanel, _llmInputs, LLMProviderRegistry.Get(provider).Fields, _settings.LoadLLMCredentials(provider));
        }
    }

    private static void RenderFields(WpfPanel panel, Dictionary<string, WpfControl> inputs, IReadOnlyList<CredentialField> fields, Dictionary<string, string> values)
    {
        panel.Children.Clear();
        inputs.Clear();
        foreach (var field in fields)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = field.Label,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            });

            WpfControl input;
            if (field.IsSecure)
            {
                var box = new PasswordBox { MinHeight = 30 };
                if (values.TryGetValue(field.Key, out var value)) box.Password = value;
                input = box;
            }
            else
            {
                var box = new WpfTextBox { MinHeight = 30 };
                box.Text = values.TryGetValue(field.Key, out var value) ? value : field.DefaultValue;
                input = box;
            }

            Grid.SetColumn(input, 1);
            row.Children.Add(input);
            panel.Children.Add(row);
            inputs[field.Key] = input;
        }
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (AsrProviderCombo.SelectedValue is ASRProvider asrProvider)
        {
            _settings.SaveASRCredentials(asrProvider, ReadInputs(_asrInputs));
        }
        if (LlmProviderCombo.SelectedValue is LLMProvider llmProvider)
        {
            _settings.SaveLLMCredentials(llmProvider, ReadInputs(_llmInputs));
        }
        if (PunctuationModeCombo.SelectedValue is PunctuationMode punctuationMode)
        {
            _settings.PunctuationMode = punctuationMode;
        }
        SetStatus("设置已保存，已隐藏到托盘。");
        Hide();
    }

    private async void TestAsrButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (AsrProviderCombo.SelectedValue is not ASRProvider provider)
        {
            SetStatus("语音模型测试失败：还没有选择语音识别服务。");
            return;
        }

        TestAsrButton.IsEnabled = false;
        SetStatus("正在测试语音模型...");
        try
        {
            var descriptor = ASRProviderRegistry.Get(provider);
            var config = descriptor.CreateConfig(ReadInputs(_asrInputs));
            if (config is null || !config.IsValid)
            {
                SetStatus("语音模型测试失败：配置不完整，请先填写必要字段。");
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var recognizer = descriptor.CreateClient();
            await recognizer.ConnectAsync(config, new ASRRequestOptions(GetSelectedPunctuationMode()), timeout.Token);
            await recognizer.SendAudioAsync(CreateSilentPcmForTest(provider), timeout.Token);
            await recognizer.EndAudioAsync(timeout.Token);
            await WaitForRecognizerCompletionAsync(recognizer, timeout.Token);
            SetStatus($"语音模型测试通过：{descriptor.DisplayName} 可用。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("语音模型测试失败：请求超时，请检查网络、代理或服务商状态。");
        }
        catch (Exception ex)
        {
            SetStatus("语音模型测试失败：" + ex.Message);
        }
        finally
        {
            TestAsrButton.IsEnabled = true;
        }
    }

    private async void TestLlmButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LlmProviderCombo.SelectedValue is not LLMProvider provider)
        {
            SetStatus("文本模型测试失败：还没有选择文本处理服务。");
            return;
        }

        TestLlmButton.IsEnabled = false;
        SetStatus("正在测试文本模型...");
        try
        {
            var descriptor = LLMProviderRegistry.Get(provider);
            var config = descriptor.CreateConfig(ReadInputs(_llmInputs));
            if (config is null)
            {
                SetStatus("文本模型测试失败：配置不完整，请先填写接口密钥。");
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = descriptor.CreateClient();
            var result = await client.ProcessAsync(
                "OK",
                "请只返回输入文本，不要添加解释：{text}",
                config,
                PromptContext.Empty,
                timeout.Token);
            if (string.IsNullOrWhiteSpace(result))
            {
                SetStatus("文本模型测试失败：模型返回为空。");
                return;
            }

            SetStatus($"文本模型测试通过：{descriptor.DisplayName} 已返回结果。");
        }
        catch (OperationCanceledException)
        {
            SetStatus("文本模型测试失败：请求超时，请检查网络、代理或服务商状态。");
        }
        catch (Exception ex)
        {
            SetStatus("文本模型测试失败：" + ex.Message);
        }
        finally
        {
            TestLlmButton.IsEnabled = true;
        }
    }

    private void RenderHotkeys()
    {
        HotkeysPanel.Children.Clear();
        _hotkeyInputs.Clear();

        foreach (var mode in _settings.LoadModes())
        {
            var displayModeName = LocalizeModeName(mode.Name);
            var container = new Border
            {
                Background = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 228, 236)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            container.Child = row;

            row.Children.Add(new TextBlock
            {
                Text = displayModeName,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            var hotkeyBox = new WpfTextBox
            {
                Text = FormatHotkey(mode.HotkeyModifiers, mode.HotkeyVirtualKey),
                IsReadOnly = true,
                MinHeight = 30,
                Margin = new Thickness(0, 0, 16, 0),
                Padding = new Thickness(8, 4, 8, 4),
                ToolTip = "点击后直接按新的快捷键组合"
            };
            Grid.SetColumn(hotkeyBox, 1);
            row.Children.Add(hotkeyBox);

            var styleBox = new WpfComboBox
            {
                ItemsSource = new[]
                {
                    new ComboItem<HotkeyStyle>("按一下开始/停止", HotkeyStyle.Toggle),
                    new ComboItem<HotkeyStyle>("按住录音，松开结束", HotkeyStyle.Hold)
                },
                SelectedValuePath = nameof(ComboItem<HotkeyStyle>.Value),
                DisplayMemberPath = nameof(ComboItem<HotkeyStyle>.Label),
                SelectedValue = mode.HotkeyStyle,
                MinHeight = 30,
                Margin = new Thickness(0)
            };
            Grid.SetColumn(styleBox, 2);
            row.Children.Add(styleBox);

            HotkeysPanel.Children.Add(container);
            var controls = new HotkeyControls
            {
                Hotkey = hotkeyBox,
                Style = styleBox,
                Modifiers = mode.HotkeyModifiers,
                VirtualKey = mode.HotkeyVirtualKey
            };
            hotkeyBox.PreviewMouseDown += (_, args) => BeginHotkeyCapture(args, controls, displayModeName);
            hotkeyBox.PreviewKeyDown += (_, args) => CaptureHotkey(args, controls, displayModeName);
            hotkeyBox.PreviewKeyUp += (_, args) => CaptureModifierOnlyHotkey(args, controls, displayModeName);
            _hotkeyInputs[mode.Id] = controls;
        }
    }

    private void BeginHotkeyCapture(MouseButtonEventArgs args, HotkeyControls controls, string modeName)
    {
        controls.IsCapturing = true;
        controls.PendingModifierKey = null;
        controls.Hotkey.Focus();
        controls.Hotkey.SelectAll();
        SetStatus($"正在录入 {modeName} 快捷键：请直接按组合键。");
        args.Handled = true;
    }

    private void SaveHotkeysButton_OnClick(object sender, RoutedEventArgs e)
    {
        var modes = _settings.LoadModes().Select(mode =>
        {
            if (!_hotkeyInputs.TryGetValue(mode.Id, out var controls))
            {
                return mode;
            }

            var modifiers = controls.Modifiers;
            var key = controls.VirtualKey;
            var style = controls.Style.SelectedValue is HotkeyStyle selectedStyle ? selectedStyle : mode.HotkeyStyle;
            return mode with
            {
                HotkeyModifiers = modifiers,
                HotkeyVirtualKey = key,
                HotkeyStyle = style
            };
        }).ToList();

        if (modes.Any(mode => mode.HotkeyModifiers == 0 &&
            mode.HotkeyVirtualKey is < 0x70 or > 0x7B &&
            !IsModifierVirtualKey(mode.HotkeyVirtualKey)))
        {
            SetStatus("快捷键保存失败：字母、数字、空格必须至少搭配 Ctrl、Alt、Shift 或 Win；单个 Ctrl/Alt/Shift/Win 可以直接使用。");
            return;
        }

        var duplicate = modes
            .GroupBy(mode => (mode.HotkeyModifiers, mode.HotkeyVirtualKey))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            SetStatus("快捷键保存失败：不能给多个模式设置同一个快捷键。");
            return;
        }

        _settings.SaveModes(modes);
        ModesChanged?.Invoke();
        SetStatus("快捷键已保存并立即生效。");
    }

    private void CaptureHotkey(System.Windows.Input.KeyEventArgs args, HotkeyControls controls, string modeName)
    {
        if (!controls.IsCapturing)
        {
            return;
        }

        args.Handled = true;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.ImeProcessed)
        {
            key = args.ImeProcessedKey;
        }

        if (key == Key.Escape)
        {
            controls.IsCapturing = false;
            controls.PendingModifierKey = null;
            controls.Hotkey.Text = FormatHotkey(controls.Modifiers, controls.VirtualKey);
            SetStatus($"{modeName} 快捷键录入已取消。");
            return;
        }

        if (IsModifierKey(key))
        {
            controls.PendingModifierKey = key;
            controls.Hotkey.Text = "松开可保存单键，或继续按主键...";
            return;
        }

        var virtualKey = ToVirtualKey(key);
        if (virtualKey <= 0)
        {
            SetStatus("这个按键暂不支持作为快捷键。");
            return;
        }

        var modifiers = ToNativeModifiers(Keyboard.Modifiers);
        controls.PendingModifierKey = null;
        controls.VirtualKey = virtualKey;
        controls.Modifiers = modifiers;
        controls.IsCapturing = false;
        controls.Hotkey.Text = FormatHotkey(modifiers, virtualKey);
        SetStatus($"{modeName} 快捷键已录入：{controls.Hotkey.Text}。点击“保存快捷键”后生效。");
    }

    private void CaptureModifierOnlyHotkey(System.Windows.Input.KeyEventArgs args, HotkeyControls controls, string modeName)
    {
        if (!controls.IsCapturing)
        {
            return;
        }

        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key == Key.ImeProcessed)
        {
            key = args.ImeProcessedKey;
        }

        if (controls.PendingModifierKey != key || !IsModifierKey(key))
        {
            return;
        }

        args.Handled = true;
        var virtualKey = ToVirtualKey(key);
        controls.PendingModifierKey = null;
        controls.VirtualKey = virtualKey;
        controls.Modifiers = 0;
        controls.IsCapturing = false;
        controls.Hotkey.Text = FormatHotkey(0, virtualKey);
        SetStatus($"{modeName} 快捷键已录入：{controls.Hotkey.Text}。点击“保存快捷键”后生效。");
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static int ToVirtualKey(Key key) => key switch
    {
        Key.LeftCtrl => NativeVirtualKeys.LControl,
        Key.RightCtrl => NativeVirtualKeys.RControl,
        Key.LeftShift => NativeVirtualKeys.LShift,
        Key.RightShift => NativeVirtualKeys.RShift,
        Key.LeftAlt => NativeVirtualKeys.LMenu,
        Key.RightAlt => NativeVirtualKeys.RMenu,
        Key.LWin => NativeVirtualKeys.LWin,
        Key.RWin => NativeVirtualKeys.RWin,
        _ => KeyInterop.VirtualKeyFromKey(key)
    };

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var result = 0u;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= NativeHotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= NativeHotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= NativeHotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= NativeHotkeyModifiers.Win;
        return result;
    }

    private static string FormatHotkey(uint modifiers, int virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeHotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeHotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & NativeHotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & NativeHotkeyModifiers.Win) != 0) parts.Add("Win");
        parts.Add(FormatVirtualKey(virtualKey));
        return string.Join("+", parts);
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey == NativeVirtualKeys.LControl) return "左 Ctrl";
        if (virtualKey == NativeVirtualKeys.RControl) return "右 Ctrl";
        if (virtualKey == NativeVirtualKeys.LShift) return "左 Shift";
        if (virtualKey == NativeVirtualKeys.RShift) return "右 Shift";
        if (virtualKey == NativeVirtualKeys.LMenu) return "左 Alt";
        if (virtualKey == NativeVirtualKeys.RMenu) return "右 Alt";
        if (virtualKey == NativeVirtualKeys.LWin) return "左 Win";
        if (virtualKey == NativeVirtualKeys.RWin) return "右 Win";
        if (virtualKey == NativeVirtualKeys.Space) return "空格";
        if (virtualKey is >= 0x70 and <= 0x7B) return "F" + (virtualKey - 0x6F);
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        return "虚拟键 " + virtualKey;
    }

    private static bool IsModifierVirtualKey(int virtualKey) =>
        virtualKey is NativeVirtualKeys.Control or NativeVirtualKeys.LControl or NativeVirtualKeys.RControl or
            NativeVirtualKeys.Shift or NativeVirtualKeys.LShift or NativeVirtualKeys.RShift or
            NativeVirtualKeys.Menu or NativeVirtualKeys.LMenu or NativeVirtualKeys.RMenu or
            NativeVirtualKeys.LWin or NativeVirtualKeys.RWin;

    private static string LocalizeModeName(string? modeName) => modeName switch
    {
        null or "" => "",
        "Prompt 优化" => "提示词优化",
        _ => modeName
    };

    private static string LocalizeHistoryStatus(string status) => status switch
    {
        "completed" => "已完成",
        "timeout" => "超时",
        "failed" => "失败",
        "stopped" => "已停止",
        "canceled" or "cancelled" => "已取消",
        _ => status
    };

    private static string LocalizeProvider(string? provider) => provider switch
    {
        null or "" => "",
        "Volcano" => "火山引擎",
        "OpenAICompatible" => "OpenAI 兼容",
        _ => provider
    };

    private static Dictionary<string, string> ReadInputs(Dictionary<string, WpfControl> inputs)
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, control) in inputs)
        {
            result[key] = control switch
            {
                WpfTextBox box => box.Text,
                PasswordBox box => box.Password,
                _ => ""
            };
        }
        return result;
    }

    private PunctuationMode GetSelectedPunctuationMode() =>
        PunctuationModeCombo.SelectedValue is PunctuationMode punctuationMode
            ? punctuationMode
            : _settings.PunctuationMode;

    private static byte[] CreateSilentPcmForTest(ASRProvider provider) =>
        new byte[provider == ASRProvider.OpenAI ? 32_000 : 6_400];

    private static async Task WaitForRecognizerCompletionAsync(ISpeechRecognizer recognizer, CancellationToken cancellationToken)
    {
        await foreach (var evt in recognizer.Events.WithCancellation(cancellationToken))
        {
            if (evt is RecognitionEvent.Error error)
            {
                throw error.Exception;
            }

            if (evt is RecognitionEvent.Completed)
            {
                return;
            }
        }
    }

    private void RefreshHistoryButton_OnClick(object sender, RoutedEventArgs e) => RefreshHistory();

    private void ExportCsvButton_OnClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_history.ExportCsv());
        SetStatus("历史记录 CSV 已复制到剪贴板。");
    }

    private void RefreshHistory()
    {
        HistoryGrid.ItemsSource = _history.FetchPage(200).Select(HistoryRow.FromRecord).ToList();
    }

    private sealed record ComboItem<T>(string Label, T Value);
    private sealed record HistoryRow(
        string CreatedAt,
        string ProcessingMode,
        string RawText,
        string FinalText,
        string Status,
        string ASRProvider)
    {
        public static HistoryRow FromRecord(HistoryRecord record) => new(
            record.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            LocalizeModeName(record.ProcessingMode),
            record.RawText,
            record.FinalText,
            LocalizeHistoryStatus(record.Status),
            LocalizeProvider(record.ASRProvider));
    }

    private sealed class HotkeyControls
    {
        public required WpfTextBox Hotkey { get; init; }
        public required WpfComboBox Style { get; init; }
        public required uint Modifiers { get; set; }
        public required int VirtualKey { get; set; }
        public Key? PendingModifierKey { get; set; }
        public bool IsCapturing { get; set; }
    }
}
