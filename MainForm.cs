using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DNSProxyGUI
{
    public partial class MainForm : Form
    {
        private bool isRunning = false;
        private UdpClient? udpServer;
        private IPAddress? upstreamDnsServer;

        public MainForm()
        {
            InitializeComponent();
            InitializeCustomComponents();
        }

        private void InitializeComponent()
        {
            this.SuspendLayout();
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.MinimumSize = new System.Drawing.Size(600, 300);
            this.Name = "MainForm";
            this.Text = "DNS Proxy GUI";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(MainForm_FormClosing);
            this.ResumeLayout(false);
        }

        private void InitializeCustomComponents()
        {
            TableLayoutPanel mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1
            };
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            TableLayoutPanel topPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 5,
                RowCount = 1,
                Padding = new Padding(10, 5, 10, 5)
            };

            Label portLabel = new Label { Text = "Port:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
            TextBox portTextBox = new TextBox { Name = "portTextBox", Text = "53", Dock = DockStyle.Fill };

            Label upstreamLabel = new Label { Text = "Upstream DNS:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
            TextBox upstreamTextBox = new TextBox { Name = "upstreamTextBox", Text = "8.8.8.8", Dock = DockStyle.Fill };

            Button startStopButton = new Button { Text = "Start", Dock = DockStyle.Fill };
            startStopButton.Click += StartStopButton_Click;

            topPanel.Controls.Add(portLabel, 0, 0);
            topPanel.Controls.Add(portTextBox, 1, 0);
            topPanel.Controls.Add(upstreamLabel, 2, 0);
            topPanel.Controls.Add(upstreamTextBox, 3, 0);
            topPanel.Controls.Add(startStopButton, 4, 0);

            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));

            mainPanel.Controls.Add(topPanel, 0, 0);

            ListView logListView = new ListView
            {
                Name = "logListView",
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
            };
            
            logListView.Columns.Add("IP Address", 200);
            logListView.Columns.Add("Hostname", 300);
            
            mainPanel.Controls.Add(logListView, 0, 1);

            this.Controls.Add(mainPanel);

            this.Resize += (sender, e) => {
                int totalWidth = logListView.ClientSize.Width - 4;
                int ipColumnWidth = Math.Min(200, totalWidth / 3);
                logListView.Columns[0].Width = ipColumnWidth;
                logListView.Columns[1].Width = totalWidth - ipColumnWidth;
            };
        }

        private async void StartStopButton_Click(object? sender, EventArgs e)
        {
            if (sender is not Button button) return;

            var portTextBox = this.Controls.Find("portTextBox", true).FirstOrDefault() as TextBox;
            var upstreamTextBox = this.Controls.Find("upstreamTextBox", true).FirstOrDefault() as TextBox;

            if (!isRunning)
            {
                if (portTextBox != null && upstreamTextBox != null &&
                    int.TryParse(portTextBox.Text, out int port) &&
                    IPAddress.TryParse(upstreamTextBox.Text, out IPAddress upstreamIp))
                {
                    try
                    {
                        udpServer = new UdpClient(port);
                        upstreamDnsServer = upstreamIp;
                        isRunning = true;
                        button.Text = "Stop";
                        button.BackColor = Color.Green;

                        // Perform reverse DNS lookup for the upstream DNS server
                        string upstreamHostname = await ReverseDnsLookupAsync(upstreamIp);
                        AddLogEntry(upstreamIp.ToString(), upstreamHostname);

                        await StartDnsProxy();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error starting DNS proxy: {ex.Message}");
                    }
                }
                else
                {
                    MessageBox.Show("Invalid port or upstream DNS IP address.");
                }
            }
            else
            {
                StopDnsProxy();
                button.Text = "Start";
                button.BackColor = SystemColors.Control;
            }
        }

        private async Task<string> ReverseDnsLookupAsync(IPAddress ip)
        {
            try
            {
                IPHostEntry entry = await Dns.GetHostEntryAsync(ip);
                return entry.HostName;
            }
            catch
            {
                return "Reverse lookup failed";
            }
        }

        private async Task StartDnsProxy()
        {
            while (isRunning && udpServer != null)
            {
                try
                {
                    UdpReceiveResult result = await udpServer.ReceiveAsync();
                    _ = HandleDnsRequestAsync(result);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error receiving DNS request: {ex.Message}");
                }
            }
        }

        private async Task HandleDnsRequestAsync(UdpReceiveResult result)
        {
            try
            {
                using var localUdpServer = udpServer;
                if (localUdpServer == null || upstreamDnsServer == null)
                {
                    Console.WriteLine("UDP server or upstream DNS is not initialized.");
                    return;
                }

                byte[] requestData = result.Buffer;
                if (requestData.Length < 12) return;

                string hostname = ExtractHostname(requestData);
                AddLogEntry("Resolving...", hostname);

                byte[] responseData = await ForwardDnsRequest(requestData, upstreamDnsServer);
                
                string ipAddress = ParseDnsResponse(responseData);

                UpdateLogEntry(ipAddress, hostname);

                await localUdpServer.SendAsync(responseData, responseData.Length, result.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling DNS request: {ex.Message}");
            }
        }

        private async Task<byte[]> ForwardDnsRequest(byte[] request, IPAddress upstreamDns)
        {
            using (var client = new UdpClient())
            {
                await client.SendAsync(request, request.Length, new IPEndPoint(upstreamDns, 53));
                var result = await client.ReceiveAsync();
                return result.Buffer;
            }
        }

        private string ParseDnsResponse(byte[] response)
        {
            if (response.Length < 12) return "Invalid response";

            int answerCount = (response[6] << 8) | response[7];
            if (answerCount == 0) return "No answer";

            int ptr = 12;
            while (ptr < response.Length && response[ptr] != 0) ptr++;
            ptr += 5;

            for (int i = 0; i < answerCount; i++)
            {
                ptr += 2;
                int type = (response[ptr] << 8) | response[ptr + 1];
                ptr += 8;
                int rdLength = (response[ptr] << 8) | response[ptr + 1];
                ptr += 2;

                if (type == 1 && rdLength == 4)
                {
                    return $"{response[ptr]}.{response[ptr + 1]}.{response[ptr + 2]}.{response[ptr + 3]}";
                }

                ptr += rdLength;
            }

            return "No A record found";
        }

        private string ExtractHostname(byte[] requestData)
        {
            int offset = 12;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            while (offset < requestData.Length)
            {
                int length = requestData[offset++];
                if (length == 0) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(System.Text.Encoding.ASCII.GetString(requestData, offset, length));
                offset += length;
            }
            return sb.ToString();
        }

        private void AddLogEntry(string ipAddress, string hostname)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => AddLogEntry(ipAddress, hostname)));
                return;
            }

            if (this.Controls.Find("logListView", true).FirstOrDefault() is ListView logListView)
            {
                ListViewItem item = new ListViewItem(new[] { ipAddress, hostname });
                
                // Set the color of the IP address
                item.UseItemStyleForSubItems = false;
                if (!IsValidIpAddress(ipAddress))
                {
                    item.SubItems[0].ForeColor = ColorTranslator.FromHtml("#808080");
                }
                else
                {
                    item.SubItems[0].ForeColor = logListView.ForeColor;
                }

                logListView.Items.Add(item);

                if (logListView.Items.Count > 0)
                {
                    logListView.EnsureVisible(logListView.Items.Count - 1);
                }
            }
        }

        private void UpdateLogEntry(string ipAddress, string hostname)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => UpdateLogEntry(ipAddress, hostname)));
                return;
            }

            if (this.Controls.Find("logListView", true).FirstOrDefault() is ListView logListView)
            {
                foreach (ListViewItem item in logListView.Items)
                {
                    if (item.SubItems[1].Text == hostname && item.SubItems[0].Text == "Resolving...")
                    {
                        item.SubItems[0].Text = ipAddress;
                        
                        // Set the color of the IP address
                        item.UseItemStyleForSubItems = false;
                        if (!IsValidIpAddress(ipAddress))
                        {
                            item.SubItems[0].ForeColor = ColorTranslator.FromHtml("#808080");
                        }
                        else
                        {
                            item.SubItems[0].ForeColor = logListView.ForeColor;
                        }
                        
                        break;
                    }
                }
            }
        }

        private void StopDnsProxy()
        {
            isRunning = false;
            var server = Interlocked.Exchange(ref udpServer, null);
            server?.Close();
        }

        private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            StopDnsProxy();
        }

        // Add this helper method to check if a string is a valid IP address
        private bool IsValidIpAddress(string ipAddress)
        {
            return System.Net.IPAddress.TryParse(ipAddress, out _);
        }
    }
}