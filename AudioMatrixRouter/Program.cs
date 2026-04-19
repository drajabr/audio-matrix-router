namespace AudioMatrixRouter;

static class Program
{
    private const string SingleInstanceMutexName = "AudioMatrixRouter.SingleInstance";

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

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startMinimized));
    }
}
