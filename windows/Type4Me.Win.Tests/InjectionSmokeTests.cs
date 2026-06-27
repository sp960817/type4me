using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Type4Me.Win.Core;
using Type4Me.Win.Services;

namespace Type4Me.Win.Tests;

public sealed class InjectionSmokeTests
{
    [Fact(Skip = "Requires an interactive foreground desktop; covered by manual Windows smoke testing.")]
    public async Task InjectAsync_SendsUnicodeTextToFocusedTextBox()
    {
        var result = await RunOnStaThread(async () =>
        {
            var box = new TextBox { Width = 420, Height = 80 };
            var window = new Window
            {
                Title = "Type4Me injection smoke test",
                Content = box,
                Width = 480,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true
            };

            window.Show();
            window.Activate();
            var targetWindow = new WindowInteropHelper(window).EnsureHandle();
            ShowWindow(targetWindow, 5);
            BringWindowToTop(targetWindow);
            SetForegroundWindow(targetWindow);
            box.Focus();
            FocusManager.SetFocusedElement(window, box);
            Keyboard.Focus(box);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);

            var service = new TextInjectionService(Dispatcher.CurrentDispatcher);
            service.CapturePromptContext(targetWindow);

            var outcome = await service.InjectAsync("TYPE4ME_INPUT_TEST 中文", CancellationToken.None);
            await Task.Delay(250);

            var text = box.Text;
            window.Close();
            return (outcome, text);
        });

        Assert.Equal(InjectionOutcome.Inserted, result.outcome);
        Assert.Equal("TYPE4ME_INPUT_TEST 中文", result.text);
    }

    private static Task<T> RunOnStaThread<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    completion.SetResult(await action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
