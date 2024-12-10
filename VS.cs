using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace Keyence
{
    public partial class VS
    {
        public delegate void ReceiveHandler(VS sender, int port, string text);

        private ILogger _logger;

        private Interlocked_Bool _Enabled = new Interlocked_Bool();
        public bool Enabled { get => _Enabled.Value; set { if (_Enabled.Exchange(value) != value) Close(conn1, conn2); } }

        private Interlocked<IPAddress> _IPAddress = new Interlocked<IPAddress>();
        public IPAddress IPAddress { get => _IPAddress.Value; set { if (!object.Equals(value, _IPAddress.Exchange(value))) Close(conn1, conn2); } }

        private Interlocked_Int32 _Port1 = new Interlocked_Int32();
        public int Port1 { get => _Port1.Value; set { if (_Port1.Exchange(value) != value) Close(conn1); } }

        private Interlocked_Int32 _Port2 = new Interlocked_Int32();
        public int Port2 { get => _Port2.Value; set { if (_Port2.Exchange(value) != value) Close(conn2); } }



        private Interlocked<TcpClient> conn1 = new Interlocked<TcpClient>();
        private Interlocked<TcpClient> conn2 = new Interlocked<TcpClient>();
        private SyncList<(int, string)> recv = new SyncList<(int, string)>();


        public VS(ILogger<VS> logger)
        {
            _logger = logger;
            Task.Run(() => Run(conn1, _Port1));
            Task.Run(() => Run(conn2, _Port2));
        }

        private void Close(params Interlocked<TcpClient>[] args)
        {
            try
            {
                foreach (var conn1 in args)
                    using (var conn2 = conn1.Exchange(null))
                        if (conn2 != null)
                            conn2.Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        private async Task Run(Interlocked<TcpClient> conn, Interlocked_Int32 port)
        {
            byte[] buff1 = new byte[1024];
            StringBuilder buff2 = new StringBuilder();
            for (; ; await Task.Delay(1))
            {
                var _ip = IPAddress;
                var _port = port.Value;
                if (Enabled && _ip != null && _port > 0)
                {
                    Close(conn);
                    buff2.Clear();

                    try
                    {
                        var _conn = new TcpClient();
                        _logger.LogInformation($"Connecting to {_ip}:{_port} ...");
                        await _conn.ConnectAsync(_ip, _port);
                        conn.Value = _conn;
                        _logger.LogInformation($"{_ip}:{_port} connected.");
                        while (Enabled && _conn.Connected)
                        {
                            int recv = await _conn.Client.ReceiveAsync(buff1, SocketFlags.None);
                            string text = Encoding.ASCII.GetString(buff1, 0, recv);
                            foreach (var c in text)
                            {
                                if (c == '\r' || c == '\n')
                                {
                                    if (buff2.Length > 0)
                                    {
                                        this.recv.Add((_port, buff2.ToString()), OnRecv);
                                        buff2.Clear();
                                    }
                                }
                                else
                                    buff2.Append(c);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ex.Message);
                        await Task.Delay(500);
                    }
                }
            }
        }

        public event ReceiveHandler Receive;

        private Task OnRecv((int, string) n)
        {
            int port = n.Item1;
            string text = n.Item2;
            try { Receive?.Invoke(this, port, text); } catch (Exception ex) { }
            //Console.WriteLine($"{port}\t{text}");
            return Task.CompletedTask;
        }



        // commands

        /// <summary>發行觸發</summary>
        /// <remarks>針對等待觸發的［拍攝］工具，發行指定的觸發訊號。</remarks>
        public void TRG()
        {
        }

        /// <summary>觸發器輸入禁止</summary>
        /// <remarks>控制觸發輸入的禁止/允許。</remarks>
        public void TD()
        {
        }

        /// <summary>讀取觸發輸入許可狀態</summary>
        /// <remarks>讀出是否為可輸入觸發的狀態。以取得了指令、I/O端子、Fieldbus狀態的OR的值來判定可輸入觸發的狀態。</remarks>
        public void TSR()
        {
        }

        /// <summary>重置</summary>
        /// <remarks>
        /// 會執行以下所有項目。
        /// •將系統屬性全部初始化，全部清除包含映像的各種快取。
        /// •清除綜合判定。
        /// •全部清除工具和任務的執行結果。
        /// •全部初始化輸出端子。
        /// •全部清除Fieldbus的輸出範圍。
        /// •解除工具的等待觸發。
        /// •新建儲存資料檔的檔案名稱。
        /// •返回主任務的開頭。
        /// •全部清除歷史資料。
        /// •全部清除統計資料。
        /// •清除透過TD指令設定的禁止觸發狀態。
        /// •清除透過OD指令設定的禁止輸出狀態。
        /// </remarks>
        public void RS()
        {
        }

        /// <summary>重新啟動</summary>
        /// <remarks>重新啟動本機。</remarks>
        public void RB()
        {
        }

        /// <summary>遷移至運行模式</summary>
        /// <remarks>從設定模式切換至運轉模式。</remarks>
        public void RUN()
        {
        }

        /// <summary>遷移至設定模式</summary>
        /// <remarks>從運轉模式切換至設定模式。</remarks>
        public void SET()
        {
        }

        /// <summary>讀出運作/設定模式</summary>
        /// <remarks>讀出目前動作模式（運作模式/設定模式）。</remarks>
        public void MOR()
        {
        }

        /// <summary>輸出禁止</summary>
        /// <remarks>控制向外部設備的資料輸出。</remarks>
        public void OD()
        {
        }

        /// <summary>切換偵測程序(編號指定)</summary>
        /// <remarks>將目前設定的偵測程式切換為指定記憶體內的偵測程式編號的偵測程式。</remarks>
        public void PL()
        {
        }

        /// <summary>讀出偵測程式(編號指定)</summary>
        /// <remarks>傳回目前讀取中的偵測設定的記憶體類別、偵測設定No.。</remarks>
        public void PR()
        {
        }

        /// <summary>保存檢測程序</summary>
        /// <remarks>儲存目前檢測程序。</remarks>
        public void PS()
        {
        }

        /// <summary>切換模板影像（編號指定）</summary>
        /// <remarks>將指定工具中使用的模板影像，切換為指定編號的模板影像並更新影像基準資訊。</remarks>
        public void MS()
        {
        }

        /// <summary>讀出模板影像(編號指定)</summary>
        /// <remarks>讀出指定工具所使用的模板影像的模板影像編號。</remarks>
        public void MR()
        {
        }

        /// <summary>更新位置補正基準值</summary>
        /// <remarks>將指定的［影像位置補正］工具的位置補正基準值，更新為目前參考工具的最新位置資訊。</remarks>
        public void RPU()
        {
        }

        /// <summary>更新圖形訊息</summary>
        /// <remarks>使用設定的模板圖像，更新指定工具上註冊的圖形資訊。</remarks>
        public void PDU()
        {
        }

        /// <summary>回應</summary>
        /// <remarks>對外部設備發送的數值進行直接應答。</remarks>
        public void EC()
        {
        }

        /// <summary>清除錯誤</summary>
        /// <remarks>清除指定類別（Error0或Error1）的錯誤狀態、錯誤代碼。</remarks>
        public void ERC()
        {
        }

        /// <summary>日期和時間設定寫入</summary>
        /// <remarks>設定指定的日期及時間。</remarks>
        public void TW()
        {
        }

        /// <summary>日期和時間設定讀取</summary>
        /// <remarks>讀取目前設定的時間日期設定。</remarks>
        public void TR()
        {
        }

        /// <summary>清除後台緩存</summary>
        /// <remarks>清除後台拍攝的快取。</remarks>
        public void ICC()
        {
        }

        /// <summary>讀出硬體型號</summary>
        /// <remarks>讀出本機的系統資訊（型號）。</remarks>
        public void HMR()
        {
        }

        /// <summary>讀出韌體版本</summary>
        /// <remarks>以「.」字元將主版本的第一位、主版本的第二位、次版本分隔，讀出本機的系統資訊（ROM版本）。</remarks>
        public void FVR()
        {
        }

        /// <summary>發行外部輸入事件</summary>
        /// <remarks>發行指定編號的外部輸入事件。</remarks>
        public void SEI()
        {
        }

        /// <summary>寫入值至儲存格</summary>
        /// <remarks>寫入指定值到指定行編號、列編號的視覺化面板單元格。</remarks>
        public void CWN()
        {
        }

        /// <summary>寫入字串至單元格</summary>
        /// <remarks>寫入指定行編號和列編號的可視化面板單元格指定。</remarks>
        public void CWS()
        {
        }

        /// <summary>執行工具測試</summary>
        /// <remarks>指定工具使用與前一次執行時相同的影像重新偵測。</remarks>
        public void TT()
        {
        }

        /// <summary>判定字串更新</summary>
        /// <remarks>使用指定工具的最新識別字串，更新該工具的判定字串的內容。</remarks>
        public void JSU()
        {
        }

        /// <summary></summary>
        /// <remarks></remarks>
        public void xx()
        {
        }

        /// <summary>對照用數據更新</summary>
        /// <remarks>使用指定工具的最新讀取數據，更新該工具的對照用數據。</remarks>
        public void CRU()
        {
        }

        /// <summary>模板影像註冊（編號指定）</summary>
        /// <remarks>將指定工具的最新目前影像，儲存為指定編號的範本影像。</remarks>
        public void MG()
        {
        }

        /// <summary>寫入邏輯值至儲存格</summary>
        /// <remarks>將指定值寫入視覺化面板中指定列、指定行的儲存格。</remarks>
        public void CWB()
        {
        }

        /// <summary>機械手工具模式設置</summary>
        /// <remarks>跳轉至指定了所指定工具的影像校正模式。</remarks>
        public void RBMW()
        {
        }

        /// <summary>檢測機械手校正</summary>
        /// <remarks>對於已設定校正工具模式的工具，偵測所指定的偵測點編號。</remarks>
        public void RBCD()
        {
        }

        /// <summary>讀取機械手校正座標</summary>
        /// <remarks>對於已設定校正工具模式的工具，讀取所指定檢測點編號的機械手座標。</remarks>
        public void RBCP()
        {
        }

        /// <summary>執行機械手校正</summary>
        /// <remarks>對於已設定校正工具模式的工具，執行校正並傳回結果。</remarks>
        public void RBCE()
        {
        }

        /// <summary>讀取機械手工具模式</summary>
        /// <remarks>對於已設定為現有機械手視覺工具模式的工具，請讀取工具編號。</remarks>
        public void RBMR()
        {
        }

        /// <summary>讀取機械手狀態</summary>
        /// <remarks>讀取校正的執行結果。</remarks>
        public void RBCSR()
        {
        }

        /// <summary>更新機械手拍攝位置</summary>
        /// <remarks>將指定工具的機械手姿態參數更新為指定的值。</remarks>
        public void RBCPW()
        {
        }

        /// <summary>更新機械手座標</summary>
        /// <remarks>為機械手視覺工具模式設為座標設定模式的工具，更新機械手座標。</remarks>
        public void RBRPW()
        {
        }

        /// <summary>清除工具快取</summary>
        /// <remarks>指定工具類別或編號，清除快取。</remarks>
        public void TBC()
        {
        }

        /// <summary>複製儲存格值</summary>
        /// <remarks>指定要複製和要貼上的儲存格範圍，複製儲存格值。</remarks>
        public void CCV()
        {
        }

        /// <summary>重新連接設備</summary>
        /// <remarks>重新連接裝置。</remarks>
        public void DRC()
        {
        }

        /// <summary>輸出資料初始化</summary>
        /// <remarks>初始化輸出資料。</remarks>
        public void RSOD()
        {
        }

        /// <summary>匯出儲存格值</summary>
        /// <remarks>指定檔案編號和儲存目標範圍，匯出儲存格值。</remarks>
        public void CEV()
        {
        }

        /// <summary>導入單元格值</summary>
        /// <remarks>指定檔案編號和要匯入的儲存格的列編號/行編號，匯入儲存格值。</remarks>
        public void CIV()
        {
        }
    }
}