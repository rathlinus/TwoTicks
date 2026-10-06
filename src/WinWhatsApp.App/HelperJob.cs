using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinWhatsApp.App;

/// <summary>
/// A job object that ends the WhatsApp helper when the app ends, also when the
/// app crashes or is killed and never gets to close the helper's input.
/// </summary>
internal static class HelperJob
{
    private static readonly Lazy<nint> s_job = new(Create);

    private static nint Create()
    {
        nint job = Native.CreateJobObject(0, 0);
        if (job == 0)
        {
            return 0;
        }
        var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        // The handle stays open for the life of the app; Windows closes it at exit,
        // which ends the processes in the job.
        return job;
    }

    public static void Add(Process process)
    {
        try
        {
            if (s_job.Value != 0 && !Native.AssignProcessToJobObject(s_job.Value, process.Handle))
            {
                Log.Info($"Could not tie the helper to the app: error {Marshal.GetLastWin32Error()}");
            }
        }
        catch (InvalidOperationException)
        {
            // It already exited.
        }
    }
}
