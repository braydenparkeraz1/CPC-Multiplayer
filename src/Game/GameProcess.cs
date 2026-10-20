using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CpcMultiplayer;

internal class GameProcess
{
	private Process proc;

	private DateTime started;

	public TimeSpan Uptime
	{
		get
		{
			if (proc != null)
			{
				return DateTime.Now - started;
			}
			return TimeSpan.Zero;
		}
	}

	public int Id
	{
		get
		{
			if (proc != null)
			{
				return proc.Id;
			}
			return 0;
		}
	}

	public bool Running
	{
		get
		{
			try
			{
				return proc != null && !proc.HasExited;
			}
			catch (InvalidOperationException)
			{
				return false;
			}
		}
	}

	public event Action<string> Line;

	public event Action Exited;

	public void Kill()
	{
		try
		{
			if (Running)
			{
				proc.Kill();
			}
		}
		catch (InvalidOperationException)
		{
		}
		catch (System.ComponentModel.Win32Exception)
		{
		}
	}

	public void Start(string gameDir, string session)
	{
		// The mod reads "-mpsession <id>" to find its inbox; the game reads arguments as key/value pairs.
		ProcessStartInfo startInfo = new ProcessStartInfo(Path.Combine(gameDir, "carparkcapital.exe"), "-mpsession " + session)
		{
			WorkingDirectory = gameDir,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		proc = new Process
		{
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};
		proc.OutputDataReceived += (s, e) =>
		{
			if (e.Data != null)
			{
				Line?.Invoke(e.Data);
			}
		};
		proc.ErrorDataReceived += (s, e) =>
		{
			if (e.Data != null)
			{
				Ui.Log("[stderr] " + e.Data);
			}
		};
		proc.Exited += (s, e) => Exited?.Invoke();
		started = DateTime.Now;
		proc.Start();
		proc.BeginOutputReadLine();
		proc.BeginErrorReadLine();
	}
}
