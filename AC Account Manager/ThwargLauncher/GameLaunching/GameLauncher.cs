﻿using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Text;
using MagFilter;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ThwargLauncher
{
    public class GameLaunchResult
    {
        public bool Success;
        public int ProcessId;
    }
    
    public delegate bool ShouldStopLaunching(object sender, EventArgs e);
    /// <summary>
    /// Called by Launch Manager to actually fire off a process for one game
    /// Called on worker thread
    /// </summary>
    class GameLauncher
    {
        [DllImport("injector.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int LaunchInjected(string command_line, string working_directory, string inject_dll_path, [MarshalAs(UnmanagedType.LPStr)] string initialize_function);

        public event ShouldStopLaunching StopLaunchEvent;

        public delegate void ReportGameStatusHandler(string status);
        public event ReportGameStatusHandler ReportGameStatusEvent;
        private void ReportGameStatus(string status)
        {
            if (ReportGameStatusEvent != null)
            {
                ReportGameStatusEvent(status);
            }
        }

        private bool CheckForStop()
        {
            if (StopLaunchEvent != null)
            {
                return StopLaunchEvent(this, null);
            }
            else
            {
                return false;
            }
        }

        public GameLaunchResult LaunchGameClient(string exelocation,
            string serverName, string accountName, string password,
            string ipAddress, ServerModel.ServerEmuEnum emu, string desiredCharacter,
            ServerModel.RodatEnum rodatSetting, bool simpleLaunch)
        {
            var result = new GameLaunchResult();
            //-username "MyUsername" -password "MyPassword" -w "ServerName" -2 -3
            if (string.IsNullOrWhiteSpace(exelocation)) { throw new Exception("Empty exelocation"); }
            if (!File.Exists(exelocation)) { throw new Exception("Missing exe: " + exelocation); }
            if (string.IsNullOrWhiteSpace(serverName)) { throw new Exception("Empty serverName"); }
            if (string.IsNullOrWhiteSpace(accountName)) { throw new Exception("Empty accountName"); }
            string arg1 = accountName;
            string arg2 = password;

            string genArgs = "TODO-below";

            bool isPhat = (emu == ServerModel.ServerEmuEnum.Phat);
            
            if (isPhat)
            {
                //PHATAC
                //-h [server ip] -p [server port] -a username:password -rodat off
                int tok = ipAddress.IndexOf(':');
                if (tok < 0) { throw new Exception("Phat address missing colon in username:password specification"); }
                string ip = ipAddress.Substring(0, tok);
                string port = ipAddress.Substring(tok + 1);
                string genArgsPhatServer;
                if (rodatSetting == ServerModel.RodatEnum.On)
                {
                    genArgsPhatServer = "-h " + ip + " -p " + port + " -a " + arg1 + ":" + arg2 + " -rodat on";
                }
                else
                {
                    genArgsPhatServer = "-h " + ip + " -p " + port + " -a " + arg1 + ":" + arg2 + " -rodat off";
                }
                
                genArgs = genArgsPhatServer;
            }
            else
            {
                //ACE
                //acclient.exe -a testaccount -h 127.0.0.1:9000 -glsticketdirect testpassword
                string genArgsACEServer = "-a " + accountName + " -h " + ipAddress + " -glsticketdirect " + arg2;
                genArgs = genArgsACEServer;
            }

            string pathToFile = exelocation;
            //check if we're doing a simple launch. If we are, ignore the fancy management stuff
            bool gameReady = false;
            if (simpleLaunch)
                gameReady = true;
            Process launcherProc = null;
            LaunchControl.LaunchResponse launchResponse = null;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = pathToFile;
                startInfo.Arguments = genArgs;
                startInfo.CreateNoWindow = true;

                RecordLaunchInfo(serverName, accountName, desiredCharacter, DateTime.UtcNow);

                string charFilepath = MagFilter.FileLocations.GetCharacterFilePath();
                string launchResponseFilepath = MagFilter.FileLocations.GetCurrentLaunchResponseFilePath();
                DateTime startWait = DateTime.UtcNow;
                DateTime characterFileWrittenTime = DateTime.MaxValue;
                DateTime loginTime = DateTime.MaxValue;

                startInfo.WorkingDirectory = Path.GetDirectoryName(startInfo.FileName);

                if (ShouldWeUseDecal(simpleLaunch))
                {
                    //Start Process with Decal Injection
                    string commandLineLaunch = startInfo.FileName + " " + startInfo.Arguments;
                    string decalInjectPath = DecalInjection.GetDecalLocation();
                    string command = "DecalStartup";
                    string asheronFolder = startInfo.WorkingDirectory;
                    launcherProc = Process.GetProcessById(Convert.ToInt32(LaunchInjected(commandLineLaunch, asheronFolder, decalInjectPath, command)));
                }
                else
                {
                    //Start Process without Decal
                    launcherProc = Process.Start(startInfo);
                }
                Logger.WriteInfo(string.Format("PID = {0}", launcherProc.Id));
                launcherProc.EnableRaisingEvents = true;
                launcherProc.Exited += LauncherProc_Exited;
                if (!gameReady)
                {
                    WaitForLauncher(launcherProc);
                    int secondsTimeout = ConfigSettings.GetConfigInt("LauncherGameTimeoutSeconds", 120);
                    TimeSpan timeout = new TimeSpan(0, 0, 0, secondsTimeout);
                    while (!gameReady && (DateTime.UtcNow - startWait < timeout))
                    {
                        if (CheckForStop())
                        {
                            // User canceled
                            if (!launcherProc.HasExited)
                            {
                                launcherProc.Kill();
                            }
                            return result;
                                
                        }
                        ReportGameStatus(string.Format("Waiting for game: {0}/{1} sec",
                            (int)((DateTime.UtcNow - startWait).TotalSeconds), secondsTimeout));
                        System.Threading.Thread.Sleep(1000);
                        if (characterFileWrittenTime == DateTime.MaxValue)
                        {
                            // First we wait until DLL writes character file
                            FileInfo fileInfo = new FileInfo(charFilepath);
                            if (fileInfo.LastWriteTime.ToUniversalTime() >= startWait)
                            {
                                characterFileWrittenTime = DateTime.UtcNow;
                            }
                        }
                        else if (loginTime == DateTime.MaxValue)
                        {
                            // Now we wait until DLL logs in or user logs in interactively
                            FileInfo fileInfo = new FileInfo(launchResponseFilepath);
                            if (fileInfo.LastWriteTime.ToUniversalTime() >= startWait)
                            {
                                loginTime = DateTime.UtcNow;
                                TimeSpan maxLatency = DateTime.UtcNow - startWait;
                                launchResponse = LaunchControl.GetLaunchResponse(maxLatency);
                            }
                            else
                            {
                                if (string.IsNullOrEmpty(desiredCharacter))
                                {
                                    Logger.WriteDebug("Game Launcher waiting for launch response and desiredCharacter is empty, file time {0} and startWait {1}",
                                        fileInfo.LastWriteTime.ToUniversalTime(), startWait);
                                }
                            }
                        }
                        else
                        {
                            // Then we give it 6 more seconds to complete login
                            int loginTimeSeconds = ConfigSettings.GetConfigInt("LauncherGameLoginTime", 0);
                            if (DateTime.UtcNow >= characterFileWrittenTime.AddSeconds(loginTimeSeconds))
                            {
                                gameReady = true;
                            }
                        }
                    }
                }
            }
            catch (Exception exc)
            {
                throw new Exception(string.Format(
                    "Failed to launch program. Check path '{0}': {1}",
                    exelocation, exc.Message));
            }
            if (!gameReady)
            {
                if (launcherProc != null && !launcherProc.HasExited)
                {
                    launcherProc.Kill();
                }
            }
            if (launchResponse != null && launchResponse.IsValid)
            {
                result.Success = gameReady;
                result.ProcessId = launchResponse.ProcessId;
            }
            if (simpleLaunch)
                result.Success = true;
            return result;
        }
        private static bool ShouldWeUseDecal(bool simpleLaunch)
        {
            if (!DecalInjection.IsDecalInstalled())
            {
                // decal not installed, so we obviously don't want to try to use it
                return false;
            }
            if (simpleLaunch)
            {
                // use decal if the user chose the checkbox to use it
                return Properties.Settings.Default.InjectDecal;
            }
            else
            { // advanced mode always uses decal if possible
                return true;
            }
        }

        private void LauncherProc_Exited(object sender, EventArgs e)
        {
            //Logger.WriteInfo("The process ended successfully. sender: " + sender.ToString());
            Process p = (Process)sender;
            AppCoordinator.RemoveObsoleteProcess(p.Id);
        }

        private bool IsValidCharacterName(string characterName)
        {
            if (string.IsNullOrEmpty(characterName)) { return false; }
            if (characterName == "None") { return false; }
            return true;
        }
        private FileSystemWatcher WatchFile(string filepath)
        {
            return new FileSystemWatcher(
                Path.GetDirectoryName(filepath),
                Path.GetFileName(filepath)
                );
        }
        private void WaitForLauncher(Process launcherProc)
        {
            DateTime startUtc = DateTime.UtcNow;
            do
            {
                while (!launcherProc.HasExited)
                {
                    if(CheckForStop())
                        return;
                    launcherProc.Refresh();
                    if (launcherProc.Responding)
                    {
                        return;
                    }
                }
                ReportGameStatus(string.Format("Waiting for launcher: {0} seconds",
                    (int)((DateTime.UtcNow - startUtc).TotalSeconds)));
            } while (!launcherProc.WaitForExit(1000));
        }
        private void RecordLaunchInfo(string serverName, string accountName, string desiredCharacter, DateTime timestampUtc)
        {
            LaunchControl.RecordLaunchInfo(serverName: serverName, accountName: accountName, characterName: desiredCharacter,
                                 timestampUtc: timestampUtc);

            // TODO
            var x = LaunchControl.DebugGetLaunchInfo();
            // verify x
        }
    }
}