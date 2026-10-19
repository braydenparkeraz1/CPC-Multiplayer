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
		ProcessStartInfo processStartInfo = new ProcessStartInfo(Path.Combine(gameDir, "carparkcapital.exe"), "-mpsession " + session);
		processStartInfo.WorkingDirectory = gameDir;
		processStartInfo.UseShellExecute = false;
		processStartInfo.RedirectStandardOutput = true;
		processStartInfo.RedirectStandardError = true;
		processStartInfo.StandardOutputEncoding = Encoding.UTF8;
		processStartInfo.StandardErrorEncoding = Encoding.UTF8;
		ProcessStartInfo startInfo = processStartInfo;
		proc = new Process
		{
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};
		proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
		{
			if (e.Data != null && this.Line != null)
			{
				this.Line(e.Data);
			}
		};
		proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
		{
			if (e.Data != null)
			{
				Ui.Log("[stderr] " + e.Data);
			}
		};
		proc.Exited += delegate
		{
			if (this.Exited != null)
			{
				this.Exited();
			}
		};
		started = DateTime.Now;
		proc.Start();
		proc.BeginOutputReadLine();
		proc.BeginErrorReadLine();
	}
}
