namespace AudioMatrixRouter;

static class Program
{
    private const string SingleInstanceMutexName = "AudioMatrixRouter.SingleInstance";
    private const string AppUserModelId = "AudioMatrixRouter.Desktop";

    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint timeEndPeriod(uint uPeriod);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(string appID);

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Audio Router Matrix is already running.",
                "Audio Router Matrix",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var startMinimized = args.Any(arg =>
            string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

        timeBeginPeriod(1);
        try
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            }
            catch
            {
                // Taskbar identity can fail on older/locked-down systems; continue safely.
            }

            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            try
            {
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;
            }
            catch
            {
                // Priority elevation can fail in constrained environments; continue safely.
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(startMinimized));
        }
        finally
        {
            timeEndPeriod(1);
        }
    }
}
