﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Managed.Adb;
using Managed.Adb.IO;
using Managed.Adb.Logs;
using WindowsInput;

namespace ClipboardHost
{
    class ClipboardHandler
    {
        private Socket client;
        private byte[] data = new byte[30000];
        private int size = 6000;
        private int fileSize = 0;
        public Device device;
        private int localPort = -1;
        private NotifyIcon icon;
        private String fileName;
        private String sendFileName;
        ProgressForm progressDialog = new ProgressForm();

        public static bool AreEqualIPE(IPEndPoint e1, IPEndPoint e2)
        {
            try
            {
                return e1.Port == e2.Port && e1.Address.Equals(e2.Address);
            }
            catch
            {
                return false;
            }
        }

        static int FreeTcpPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public ClipboardHandler(String ip, int port, NotifyIcon icon)
        {
            this.icon = icon;
            connect(ip, port);
        }

        public ClipboardHandler(Device device, NotifyIcon icon)
        {
            this.icon = icon;
            this.device = device;
            //get free port
            localPort = ClipboardHandler.FreeTcpPort();
            ExecuteCmd("am start -W -n \"com.radvansky.clipboard/.MainActivity\" --ei hostPort " + localPort, device).PropertyChanged += cmdExecuted;
        }

        public bool isAlive()
        {
            if (client != null)
            {
                return client.Connected;
            }
            return false;
        }

        private void cmdExecuted(Object sender, PropertyChangedEventArgs ev)
        {
            String output = (sender as OutputReporter).Output;
            Console.WriteLine(output);
            if (output.Split('\n').Last()=="Complete")
            {
                ExecuteCmd("logcat -v raw -d CLIENT-PORT:I *:S", device).PropertyChanged += portReply;
            }
        }

        private void portReply(Object sender, PropertyChangedEventArgs ev)
        {
            String output = (sender as OutputReporter).Output;
            int portOutput = -1;
            int.TryParse(output.Split('\n').Last(),out portOutput);
            if ((portOutput != -1) && (localPort != -1))
            {
                //Try to port forward
                device.CreateForward(localPort, portOutput);
                connect("127.0.0.1", localPort);
            }
        }
        public IPEndPoint getEndpoint()
        {
            if (client!=null)
            {
                if (client.Connected)
                {
                    return client.RemoteEndPoint as IPEndPoint;
                }
            }
            return null;
        }

        public void connect(String ip, int port)
        {
            //Check if there is no such connection
            IPEndPoint iep = new IPEndPoint(IPAddress.Parse(ip), port);
            
            Socket newsock = new Socket(AddressFamily.InterNetwork,
                                  SocketType.Stream, ProtocolType.Tcp);
            
            newsock.BeginConnect(iep, new AsyncCallback(Connected), newsock);
        }

        void sendMsg(String msg)
        {
            try
            {
                byte[] message = Encoding.ASCII.GetBytes(msg);
                client.BeginSend(message, 0, message.Length, SocketFlags.None,
                             new AsyncCallback(SendData), client);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        void Connected(IAsyncResult iar)
        {
            client = (Socket)iar.AsyncState;
            try
            {
                client.EndConnect(iar);
                Console.WriteLine("Connected to: " + client.RemoteEndPoint.ToString());
                client.BeginReceive(data, 0, size, SocketFlags.None,
                             new AsyncCallback(ReceiveData), client);

            }
            catch (SocketException ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        void ReceiveData(IAsyncResult iar)
        {
            try
            {
                Socket remote = (Socket)iar.AsyncState;
                if (remote == null)
                {
                    Console.WriteLine("Error ?");
                    progressDialog.Finish();
                    fileName = null;
                    _FileStream.Close();
                    sendMsg("ack");// 'null' being the exception. The client disconnected normally in this case.
                    return;
                }
                int recv = remote.EndReceive(iar);
                if (!String.IsNullOrEmpty(fileName))
                {
                    try
                    {
                       
                        _FileStream.Write(data, 0, recv);
                        Double result = Convert.ToDouble(_FileStream.Length) / Convert.ToDouble(fileSize) * 100.0;
                        Console.WriteLine(_FileStream.Length + "/" + fileSize + "(" + recv + ")" + " " + result);
                        progressDialog.ReportProgress(Convert.ToInt32(result));
                        if (_FileStream.Length >= fileSize)
                        {
                            Console.WriteLine("Done");
                           progressDialog.Finish();
                            fileName = null;
                            fileSize = 0;
                            _FileStream.Close();
                            sendMsg("ack");
                            return;
                        }
                        else
                        {
                            client.BeginReceive(data, 0, size, SocketFlags.Partial, ReceiveData, remote);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                else
                {
                    string stringData = Encoding.UTF8.GetString(data, 0, recv);
                    Console.WriteLine(stringData);
                    processData(stringData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        void SendData(IAsyncResult iar)
        {
            try
            {
                Socket remote = (Socket)iar.AsyncState;
                int sent = remote.EndSend(iar);
                Console.WriteLine("Sending data");
                remote.BeginReceive(data, 0, size, SocketFlags.None,
                              new AsyncCallback(ReceiveData), remote);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                client.Disconnect(true);
                if (!client.Connected)
                {
                    Console.WriteLine("Disconnected");
                }
            }
        }

        private FileStream _FileStream;
        void processData(String data)
        {
            if (data!=null)
            {
                if (data.Contains("e265o00lgI"))
                {
                    saveToClipboard(data.Replace("e265o00lgI", ""), false);
                }
                else if (data.Contains("0BrvGy1AFC"))
                {
                    saveToClipboard(data.Replace("0BrvGy1AFC", ""), true);
                }
                else if (data.Contains("5WbsEg2OFW"))
                {
                    //File ACK
                    if (sendFileName != null)
                    {
                        try
                        {
                            client.SendFile(sendFileName);
                            sendFileName = null;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                    return;
                }
                else if (data.Contains("1EfsEj5RKW"))
                {
                    //save filename
                    String[] split = data.Split('|');
                    fileSize = int.Parse(split[1]);


                    fileName = split[2];
                    // Open file for reading
                    string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    if (!String.IsNullOrEmpty(Properties.Settings.Default.Dir))
                    {
                        path = Properties.Settings.Default.Dir;
                    }
                    _FileStream =
                       new FileStream(path + "\\" + fileName, FileMode.Create,
                                                FileAccess.Write);

                    sendMsg("ack");
                    //Create progress dialog
                    if (progressDialog.ShowDialog()==DialogResult.Cancel)
                        {
                        //Should cancel download
                        Console.WriteLine("Should cancel");
                        client.Disconnect(false);
                        String tmp = fileName;
                        fileName = null;
                        _FileStream.Close();
                        File.Delete(tmp);
                    }
                    return;
                }
                else
                {
                    Console.WriteLine(data);
                }
                if (Properties.Settings.Default.Mode==1)
                {
                    //Show baloon
                    if (icon != null)
                    {
                       try
                        {
                            icon.BalloonTipText = data;
                            
                            if (device != null)
                            {
                                if ((device.Model != null) && (device.SerialNumber != null))
                                {
                                    icon.BalloonTipTitle = device.Model + "(" + device.SerialNumber + ")";
                                }
                                else
                                {
                                    icon.BalloonTipTitle = "Unknown device";
                                }
                            }
                            else
                            {
                                try
                                {
                                    IPEndPoint ipe = getEndpoint();
                                    icon.BalloonTipTitle = ipe.Address.ToString() + ":" + ipe.Port;
                                }
                                catch
                                {
                                    icon.BalloonTipTitle = "Unknown device";
                                }
                            }
                            icon.ShowBalloonTip(1000);
                        }
                        catch
                        {

                        }
                    }
                }
                sendMsg("ack");
            }
            else
            {
                Console.WriteLine("Data is null");
            }
        }

        public void sendFile(String fileToSend)
        {
            try
            {
                FileInfo info = new FileInfo(fileToSend);
                if (info.Exists)
                {
                    sendMsg("1EfsEj5RKW|" + info.Length + "|" + info.Name);
                    sendFileName = fileToSend;
                    if (icon != null)
                    {
                        icon.BalloonTipText = info.Name;
                        icon.BalloonTipTitle = "File Upload";
                        icon.ShowBalloonTip(500);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private void saveToClipboard(String data, bool enter)
        {

            Thread thread = new Thread(() =>
            {
                try
                {
                    if (!String.IsNullOrEmpty(data))
                    {
                        if (Properties.Settings.Default.Mode!=1)
                        {
                            System.Media.SystemSounds.Beep.Play();
                        }
                        Clipboard.SetText(data.Replace("\n", "\r\n"));
                        //Paste
                        InputSimulator.SimulateModifiedKeyStroke(VirtualKeyCode.CONTROL, new[] { VirtualKeyCode.VK_V });

                        if (enter)
                        {
                            //Return
                            InputSimulator.SimulateKeyPress(VirtualKeyCode.RETURN);

                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
            });
            thread.SetApartmentState(ApartmentState.STA); //Set the thread to STA
            thread.Start();
            thread.Join();
        }

        private ICommandResult ExecuteCmd(string command,Device device)
        {
            var result = new OutputReporter { CommandText = command };
            result.Complete = Task.Run(() => {
                AdbHelper.Instance.ExecuteRemoteCommand(AndroidDebugBridge.SocketAddress, command, device, result);
                return result.Output;
            });
            return result;
        }

        class LogReporter : LogReceiver
        {
            public LogReporter(ILogListener listener) : base(listener)
            {
            }
        }

        class LogListener : ILogListener
        {
            public void NewData(byte[] data, int offset, int length)
            {
               //
            }

            public void NewEntry(LogEntry entry)
            {
                Console.WriteLine(entry.ToString());
            }
        }

        class OutputReporter : MultiLineReceiver, ICommandResult
        {
            public string CommandText { get; set; }
            public Task<string> Complete { get; set; }

            string output;
            public string Output
            {
                get { return output; }
                set { output = value; OnPropertyChanged(); }
            }

            ///<summary>Occurs when a property value is changed.</summary>
            public event PropertyChangedEventHandler PropertyChanged;
            ///<summary>Raises the PropertyChanged event.</summary>
            ///<param name="name">The name of the property that changed.</param>
            protected virtual void OnPropertyChanged([CallerMemberName] string name = null) => OnPropertyChanged(new PropertyChangedEventArgs(name));
            ///<summary>Raises the PropertyChanged event.</summary>
            ///<param name="e">An EventArgs object that provides the event data.</param>
            protected virtual void OnPropertyChanged(PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, e);

            protected override void ProcessNewLines(string[] lines)
            {
                // This is only called synchronously, by the blocking
                // Device.ExecuteShellCommand() method, so we have no
                // threading issues.
                Output += string.Join(Environment.NewLine, lines);
            }
        }
        ///<summary>Reports the result of a shell command executing on the device.</summary>
        interface ICommandResult : INotifyPropertyChanged
        {
            string CommandText { get; }

            ///<summary>Resolves to the complete output, after the command has exited.</summary>
            Task<string> Complete { get; }
            ///<summary>The current output of the command.  This will update as the command prints more output, and will raise <see cref="INotifyPropertyChanged.PropertyChanged"/>.</summary>
            string Output { get; }
        }
    }
}
