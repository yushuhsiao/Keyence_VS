using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Keyence
{
    public partial class VS
    {
        public delegate void DataHandler(VS sender, string text);

        private ILogger _logger;

        private Interlocked<IPAddress> _IPAddress = new Interlocked<IPAddress>();
        public IPAddress IP { get => _IPAddress.Value; set { if (!object.Equals(value, _IPAddress.Exchange(value))) CloseConnection(); } }

        private Interlocked_Int32 _Port = new Interlocked_Int32();
        public int Port { get => _Port.Value; set { if (_Port.Exchange(value) != value) CloseConnection(); } }

        public int CommandTimeout { get; set; }

        public event Action<VS> OnConnected;
        public event Action<VS> OnDisconnected;
        public event DataHandler OnReceiveData;
        public event DataHandler OnSendData;

        public bool IsConnected => connection.Value?.Connected == true;

        private Interlocked<TcpClient> connection = new Interlocked<TcpClient>();
        private Queue<string> recv_data = new Queue<string>();
        public Dictionary<string, ErrorCode?> LastErrorCode { get; } = new Dictionary<string, ErrorCode?>();
        private ErrorCode SetErr(string name, ErrorCode errorCode)
        {
            LastErrorCode[name] = errorCode;
            return errorCode;
        }

        public VS(ILogger<VS> logger)
        {
            _logger = logger;
            ThreadPool.QueueUserWorkItem(RecvQueueProc);
        }

        public void CloseConnection()
        {
            try
            {
                using (var conn = this.connection.Exchange(null))
                {
                    if (conn == null) return;
                    var ip = conn.Client?.RemoteEndPoint;
                    bool e = conn.Connected;
                    conn.Close();
                    if (e) OnDisconnected?.Invoke(this);
                    _logger.LogInformation($"{ip} disconnected");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }

        public BusyState ConnectBusy { get; } = new BusyState();
        public bool ConnectToDevice(out TcpClient tcpClient)
        {
            if (connection.GetValue(out tcpClient))
                if (tcpClient.Connected)
                    return true;
            using (ConnectBusy.Enter(out bool busy))
            {
                if (busy) return false;
                CloseConnection();
                var ip = this.IP;
                var port = this.Port;
                if (ip == null) return false;
                if (port <= 0) return false;
                try
                {
                    _logger.LogInformation($"Connecting to {ip}:{port} ...");
                    tcpClient = new TcpClient();
                    tcpClient.Connect(ip, port);
                    if (tcpClient.Connected)
                    {
                        this.connection.Value = tcpClient;
                        _logger.LogInformation($"{ip}:{port} connected.");
                        try { OnConnected?.Invoke(this); }
                        catch { }
                        var _tcpClient = tcpClient;
                        ThreadPool.QueueUserWorkItem(state =>
                        {
                            byte[] buff1 = new byte[1024];
                            StringBuilder buff2 = new StringBuilder();
                            if (_tcpClient.Connected == false) return;
                            try
                            {
                                using (_tcpClient)
                                {
                                    while (_tcpClient.Connected)
                                    {
                                        int recv = _tcpClient.Client.Receive(buff1, SocketFlags.None);
                                        if (recv == 0) break;
                                        string text = Encoding.ASCII.GetString(buff1, 0, recv);
                                        foreach (var c in text)
                                        {
                                            if (c == '\r' || c == '\n')
                                            {
                                                if (buff2.Length > 0)
                                                {
                                                    this.recv_data.Enqueue(buff2.ToString());
                                                    receiveEvent.Set();
                                                    buff2.Clear();
                                                }
                                            }
                                            else
                                                buff2.Append(c);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, ex.Message);
                            }
                        });
                        return true;
                    }
                    else
                    {
                        try { using (tcpClient) tcpClient.Close(); }
                        catch { }
                        _logger.LogInformation($"Connect to {ip}:{port} failed.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, ex.Message);
                }
                return false;
            }
        }

        private AutoResetEvent receiveEvent = new AutoResetEvent(false);
        private void RecvQueueProc(object state)
        {
            for (; ; )
            {
                if (Monitor.TryEnter(recv_data))
                {
                    try
                    {
                        while (recv_data.TryDequeue(out var text))
                        {
                            try
                            {
                                if (!text.StartsWith('{'))
                                {
                                    var txt = text.Split(',');
                                    if (txt.ER() && txt.Get(1).IsEquals(cmd_request.Value))
                                        cmd_response.Value = txt;
                                    else if (txt.Get(0).IsEquals(cmd_request.Value))
                                        cmd_response.Value = txt;
                                    else
                                    {
                                        ;
                                    }
                                }
                            }
                            catch (Exception ex) { _logger.LogError(ex, ex.Message); }

                            // invoke event
                            try { OnReceiveData?.Invoke(this, text); }
                            catch (Exception ex) { _logger.LogError(ex, ex.Message); }
                        }
                    }
                    finally { Monitor.Exit(recv_data); }
                }
                try { receiveEvent.WaitOne(5000); }
                catch { Thread.Sleep(100); }
            }
        }

        public bool IsBusy => cmd_request.IsNotNull;
        private Interlocked<string> cmd_request = new Interlocked<string>();
        private Interlocked<string[]> cmd_response = new Interlocked<string[]>();
        private Stopwatch cmd_timer = new Stopwatch();

        public bool Send(string text, out ErrorCode errorCode, out string[] result) => Send(text, null, out errorCode, out result);
        public bool Send(string text, string args, out ErrorCode errorCode, out string[] result)
        {
            result = null;
            errorCode = ErrorCode.CommandBusy;
            if (cmd_request.TrySet(text) == false)
                return false;

            cmd_response.Value = null;
            try
            {
                StringBuilder request = new StringBuilder(text);
                if (args != null)
                {
                    request.Append(',');
                    request.Append(args);
                }
                request.Append('\r');
                var data = Encoding.ASCII.GetBytes(request.ToString());
                errorCode = ErrorCode.NoConnection;
                if (ConnectToDevice(out var tcpClient) == false)
                    return false;
                cmd_timer.Restart();
                int cnt = tcpClient.Client.Send(data);
                OnSendData?.Invoke(this, text);
                while (cmd_timer.ElapsedMilliseconds < CommandTimeout)
                {
                    result = cmd_response.Exchange(null);
                    if (result == null)
                        Thread.Sleep(1);
                    else
                    {
                        var r = result.ER();
                        if (r)
                        {
                            if (r && result.TryGetValueAt(2, out var err1) && err1.ToInt32(out var err2))
                                errorCode = (ErrorCode)err2;
                            else
                                errorCode = ErrorCode.ER;
                        }
                        else
                            errorCode = ErrorCode.Success;
                        return !r;
                    }
                }
                errorCode = ErrorCode.CommandTimeout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
                errorCode = ErrorCode.Exception;
            }
            finally { cmd_request.Exchange(null); }
            return false;
        }



        // commands

        /// <summary>發行觸發</summary>
        /// <remarks>針對等待觸發的［拍攝］工具，發行指定的觸發訊號。</remarks>
        public ErrorCode TRG()
        {
            if (Send("TRG", out var err, out var res))
                return SetErr("TRG", err);
            else if (res.ER(out var err1))
                if (err1.ToInt32(out int err2))
                    err = (ErrorCode)err2;
            return SetErr("TRG", err);
        }

        /// <summary>觸發器輸入禁止</summary>
        /// <param name="enabled">
        /// 0 : Enable settings
        /// 1 : Disable settings
        /// </param>
        /// <remarks>控制觸發輸入的禁止/允許。</remarks>
        public ErrorCode TD(bool enabled)
        {
            Send($"TD", (enabled ? 0 : 1).ToString(), out var err, out var res);
            return SetErr("TD", err);
        }

        /// <summary>讀取觸發輸入許可狀態</summary>
        /// <param name="enabled">
        /// 0 : Enable settings
        /// 1 : Disable settings
        /// </param>
        /// <remarks>讀出是否為可輸入觸發的狀態。以取得了指令、I/O端子、Fieldbus狀態的OR的值來判定可輸入觸發的狀態。</remarks>
        public ErrorCode TSR(out bool enabled)
        {
            if (Send($"TSR", out var err, out var res))
            {
                if (res.TryGetValueAt(1, out var state) && state.ToInt32(out int n))
                {
                    if ((enabled = n == 1) || n == 0)
                        goto _exit;
                    else
                    {
                        err = ErrorCode.Unknown;
                        goto _exit;
                    }
                }
            }
            enabled = default;
        _exit:
            return SetErr("TSR", err);
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
        public ErrorCode RS()
        {
            Send("RS", out var err, out var res);
            return SetErr("RS", err);
        }

        /// <summary>重新啟動</summary>
        /// <remarks>重新啟動本機。</remarks>
        public ErrorCode RB()
        {
            Send("RB", out var err, out var res);
            return SetErr("RB", err);
        }

        /// <summary>遷移至運行模式</summary>
        /// <remarks>從設定模式切換至運轉模式。</remarks>
        public ErrorCode RUN()
        {
            Send("RUN", out var err, out var res);
            return SetErr("RUN", err);
        }

        /// <summary>遷移至設定模式</summary>
        /// <remarks>從運轉模式切換至設定模式。</remarks>
        public ErrorCode SET()
        {
            Send("SET", out var err, out var res);
            return SetErr("SET", err);
        }

        /// <summary>讀出運作/設定模式</summary>
        /// <param name="mode">
        /// 0 : 設定模式
        /// 1 : 運作模式
        /// </param>
        /// <remarks>讀出目前動作模式（運作模式/設定模式）。</remarks>
        public ErrorCode MOR(out int mode)
        {
            if (Send("MOR", out var err, out var res))
                res.Get(1).ToInt32(out mode);
            else
                mode = 0;
            return SetErr("MOR", err);
        }

        /// <summary>輸出禁止</summary>
        /// <param name="n">
        /// 0 : 許可
        /// 1 : 禁止
        /// </param>
        /// <remarks>控制向外部設備的資料輸出。</remarks>
        public ErrorCode OD(int n)
        {
            Send("OD", n.ToString(), out var err, out var res);
            return SetErr("OD", err);
        }

        /// <summary>切換偵測程序(編號指定)</summary>
        /// <param name="d">
        /// 1 : 內建記憶體
        /// 2 : SD卡
        /// </param>
        /// <param name="nnnn">檢測程序編號（0至999）</param>
        /// <remarks>將目前設定的偵測程式切換為指定記憶體內的偵測程式編號的偵測程式。</remarks>
        public ErrorCode PL(int d, int nnnn)
        {
            Send("PL", $"{d},{nnnn}", out var err, out var res);
            return SetErr("PL", err);
        }

        /// <summary>讀出偵測程式(編號指定)</summary>
        /// <param name="d">
        /// 1 : 內建記憶體
        /// 2 : SD卡
        /// </param>
        /// <param name="nnnn">檢測程序編號（0至999）</param>
        /// <remarks>傳回目前讀取中的偵測設定的記憶體類別、偵測設定No.。</remarks>
        public ErrorCode PR(out int d, out int nnnn)
        {
            if (Send("PR", out var err, out var res))
            {
                if (res.TryGetValueAt(1, out var _d) && res.TryGetValueAt(2, out var _nnnn))
                {
                    if (_d.ToInt32(out d) && _nnnn.ToInt32(out nnnn))
                        return err;
                    err = ErrorCode.Unknown;
                }
            }
            d = default;
            nnnn = default;
            return SetErr("PR", err);
        }

        /// <summary>保存檢測程序</summary>
        /// <remarks>儲存目前檢測程序。</remarks>
        public ErrorCode PS()
        {
            Send("PS", out var err, out var res);
            return SetErr("PS", err);
        }

        /// <summary>切換模板影像（編號指定）</summary>
        /// <param name="nnnn">
        /// 工具編號（0至1999，9999）
        /// 向工具編號指定了9999時，會對全部具有模板影像的工具執行。
        /// </param>
        /// <param name="uuu">模板圖號（0至999）</param>
        /// <remarks>將指定工具中使用的模板影像，切換為指定編號的模板影像並更新影像基準資訊。</remarks>
        public ErrorCode MS(int nnnn, int uuu)
        {
            Send("MS", $"{nnnn},{uuu}", out var err, out var res);
            return SetErr("MS", err);
        }

        /// <summary>讀出模板影像(編號指定)</summary>
        /// <param name="nnnn">工具編號（0至1999）</param>
        /// <param name="uuu">
        /// 模板影像編號（0至999、65535）
        /// 未記載所設定模板影像檔案名稱的開頭數字時，將會傳回65535。
        /// </param>
        /// <remarks>讀出指定工具所使用的模板影像的模板影像編號。</remarks>
        public ErrorCode MR(int nnnn, out int uuu)
        {
            if (Send("MR", out var err, out var res))
            {
                if (res.TryGetValueAt(1, out var _uuu))
                {
                    if (_uuu.ToInt32(out uuu))
                        return err;
                    err = ErrorCode.Unknown;
                }
            }
            uuu = default;
            return SetErr("MR", err);
        }

        /// <summary>更新位置補正基準值</summary>
        /// <param name="nnnn">
        /// 工具編號（0至1999，9999）
        /// 指定了9999時，會對所有物件工具執行。
        /// </param>
        /// <remarks>將指定的［影像位置補正］工具的位置補正基準值，更新為目前參考工具的最新位置資訊。</remarks>
        public ErrorCode RPU(int nnnn)
        {
            Send("RPU", nnnn.ToString(), out var err, out var res);
            return SetErr("RPU", err);
        }

        /// <summary>更新圖形訊息</summary>
        /// <param name="nnnn">
        /// nnnn：工具編號（0至1999、9999）
        /// 向工具編號指定了9999時，會對任務內的全部工具執行。
        /// </param>
        /// <remarks>使用設定的模板圖像，更新指定工具上註冊的圖形資訊。</remarks>
        public ErrorCode PDU(int nnnn)
        {
            Send("PDU", nnnn.ToString(), out var err, out var res);
            return SetErr("PDU", err);
        }

        /// <summary>回應</summary>
        /// <remarks>對外部設備發送的數值進行直接應答。</remarks>
        public ErrorCode EC(string str_in, out string str_out)
        {
            if (Send("EC", str_in, out var err, out var res))
            {
                if (res.TryGetValueAt(1, out str_out))
                    return err;
            }
            str_out = default;
            return SetErr("EC", err);
        }

        /// <summary>清除錯誤</summary>
        /// <param name="n">
        /// n : 錯誤狀態類別（0或1）
        ///     0 : 清除Error0 Status 與Error0 Code
        ///     1 : 清除Error1 Status 與Error1 Code
        /// </param>
        /// <remarks>清除指定類別（Error0或Error1）的錯誤狀態、錯誤代碼。</remarks>
        public ErrorCode ERC(int n)
        {
            Send("ERC", n.ToString(), out var err, out var res);
            return SetErr("ERC", err);
        }

        /// <summary>日期和時間設定寫入</summary>
        /// <remarks>設定指定的日期及時間。</remarks>
        public ErrorCode TW(DateTime time)
        {
            Send("TW", $"{time.Year},{time.Month},{time.Day},{time.Hour},{time.Minute},{time.Second}", out var err, out var res);
            return SetErr("TW", err);
        }

        /// <summary>寫入當前的日期和時間設定</summary>
        /// <remarks>設定當前的日期及時間。</remarks>
        public ErrorCode TW() => TW(DateTime.Now);

        /// <summary>日期和時間設定讀取</summary>
        /// <remarks>讀取目前設定的時間日期設定。</remarks>
        public ErrorCode TR(out DateTime time)
        {
            time = default;
            if (Send("TR", out var err, out var res))
            {
                try
                {
                    if (res.Get(1).ToInt32(out var year) &&
                        res.Get(2).ToInt32(out var month) &&
                        res.Get(3).ToInt32(out var day) &&
                        res.Get(4).ToInt32(out var hour) &&
                        res.Get(5).ToInt32(out var minute) &&
                        res.Get(6).ToInt32(out var second))
                        time = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
                }
                catch { }
            }
            return SetErr("TR", err);
        }

        /// <summary>清除後台緩存</summary>
        /// <remarks>清除後台拍攝的快取。</remarks>
        public ErrorCode ICC()
        {
            Send("ICC", out var err, out var res);
            return SetErr("ICC", err);
        }

        /// <summary>讀出硬體型號</summary>
        /// <param name="model">設備型號字串（ASCII碼）</param>
        /// <remarks>讀出本機的系統資訊（型號）。</remarks>
        public ErrorCode HMR(out string model)
        {
            if (Send("HMR", out var err, out var res))
            {
                if (res.TryGetValueAt(1, out model))
                    return err;
                err = ErrorCode.Unknown;
            }
            model = default;
            return SetErr("HMR", err);
        }

        /// <summary>讀出韌體版本</summary>
        /// <param name="version">版本的字串（「主版本的第一位.主版本的第二位.次版本的四位」字串。ASCII碼)</param>
        /// <remarks>以「.」字元將主版本的第一位、主版本的第二位、次版本分隔，讀出本機的系統資訊（ROM版本）。</remarks>
        public ErrorCode FVR(out string version)
        {
            if (Send("FVR", out var err, out var res))
            {
                if (res.TryGetValueAt(1, out version))
                    return err;
                err = ErrorCode.Unknown;
            }
            version = default;
            return SetErr("FVR", err);
        }

        /// <summary>發行外部輸入事件</summary>
        /// <param name="n">外部輸入事件編號（0至199）</param>
        /// <remarks>發行指定編號的外部輸入事件。</remarks>
        public ErrorCode SEI(int n)
        {
            Send("SEI", n.ToString(), out var err, out var res);
            return SetErr("SEI", err);
        }

        /// <summary>寫入值至儲存格</summary>
        /// <param name="cc">可視化面板的列編號（1至100）</param>
        /// <param name="rr">可視化面板的行編號（1至1000）</param>
        /// <param name="nnnn">設定值</param>
        /// <remarks>寫入指定值到指定行編號、列編號的視覺化面板單元格。</remarks>
        public ErrorCode CWN(int cc, int rr, decimal nnnn)
        {
            Send("CWN", $"{cc},{rr},{nnnn}", out var err, out var res);
            return SetErr("CWN", err);
        }

        /// <summary>寫入字串至單元格</summary>
        /// <param name="cc">可視化面板列數（1至100）</param>
        /// <param name="rr">可視化面板行號（1至1000）</param>
        /// <param name="ssss">字串</param>
        /// <remarks>寫入指定行編號和列編號的可視化面板單元格指定。</remarks>
        public ErrorCode CWS(int cc, int rr, string ssss)
        {
            Send("CWS", $"{cc},{rr},\"{ssss}\"", out var err, out var res);
            return SetErr("CWS", err);
        }

        /// <summary>執行工具測試</summary>
        /// <param name="nnnn">工具編號（0至1999）</param>
        /// <remarks>指定工具使用與前一次執行時相同的影像重新偵測。</remarks>
        public ErrorCode TT(int nnnn)
        {
            Send("TT", nnnn.ToString(), out var err, out var res);
            return SetErr("TT", err);
        }

        /// <summary>判定字串更新</summary>
        /// <param name="nnnn">工具編號（0～1999，9999）</param>
        /// <remarks>使用指定工具的最新識別字串，更新該工具的判定字串的內容。</remarks>
        public ErrorCode JSU(int nnnn)
        {
            Send("JSU", nnnn.ToString(), out var err, out var res);
            return SetErr("JSU", err);
        }

        /// <summary>對照用數據更新</summary>
        /// <param name="nnnn">工具編號（0～1999，9999）</param>
        /// <remarks>使用指定工具的最新讀取數據，更新該工具的對照用數據。</remarks>
        public ErrorCode CRU(int nnnn)
        {
            Send("CRU", nnnn.ToString(), out var err, out var res);
            return SetErr("CRU", err);
        }

        /// <summary>模板影像註冊（編號指定）</summary>
        /// <param name="nnnn">工具編號（0～1999）</param>
        /// <param name="uuu">模板影像編號（0～999）</param>
        /// <remarks>將指定工具的最新目前影像，儲存為指定編號的範本影像。</remarks>
        public ErrorCode MG(int nnnn, int uuu)
        {
            Send("MG", $"{nnnn},{uuu}", out var err, out var res);
            return SetErr("MG", err);
        }

        /// <summary>寫入邏輯值至儲存格</summary>
        /// <param name="cc">可視化面板的列編號（1～100)</param>
        /// <param name="rr">可視化面板的行編號（1～1000)</param>
        /// <param name="n">邏輯值</param>
        /// <remarks>將指定值寫入視覺化面板中指定列、指定行的儲存格。</remarks>
        public ErrorCode CWB(int cc, int rr, int n)
        {
            Send("CWB", $"{cc},{rr},{n}", out var err, out var res);
            return SetErr("CWB", err);
        }

        #region Robots

        ///// <summary>機械手工具模式設置</summary>
        ///// <remarks>跳轉至指定了所指定工具的影像校正模式。</remarks>
        //public void RBMW()
        //{
        //}

        ///// <summary>檢測機械手校正</summary>
        ///// <remarks>對於已設定校正工具模式的工具，偵測所指定的偵測點編號。</remarks>
        //public void RBCD()
        //{
        //}

        ///// <summary>讀取機械手校正座標</summary>
        ///// <remarks>對於已設定校正工具模式的工具，讀取所指定檢測點編號的機械手座標。</remarks>
        //public void RBCP()
        //{
        //}

        ///// <summary>執行機械手校正</summary>
        ///// <remarks>對於已設定校正工具模式的工具，執行校正並傳回結果。</remarks>
        //public void RBCE()
        //{
        //}

        ///// <summary>讀取機械手工具模式</summary>
        ///// <remarks>對於已設定為現有機械手視覺工具模式的工具，請讀取工具編號。</remarks>
        //public void RBMR()
        //{
        //}

        ///// <summary>讀取機械手狀態</summary>
        ///// <remarks>讀取校正的執行結果。</remarks>
        //public void RBCSR()
        //{
        //}

        ///// <summary>更新機械手拍攝位置</summary>
        ///// <remarks>將指定工具的機械手姿態參數更新為指定的值。</remarks>
        //public void RBCPW()
        //{
        //}

        ///// <summary>更新機械手座標</summary>
        ///// <remarks>為機械手視覺工具模式設為座標設定模式的工具，更新機械手座標。</remarks>
        //public void RBRPW()
        //{
        //}

        #endregion

        /// <summary>清除工具快取</summary>
        /// <param name="m">
        /// 工具類別（0～5、9999）
        ///     0：影像輸出工具
        ///     1：數據輸出工具
        ///     2：統計工具
        ///     3：成品率工具
        ///     4：數據歷史工具
        ///     5：資料輸入（無協定）工具
        ///     為工具類別指定了9999時，會對所有工具類別執行。
        ///     工具類別0：影像輸出工具中也包含附圖形影像輸出工具。
        /// </param>
        /// <param name="nnnn">
        /// 工具編號（0～1999，9999）
        ///     0：影像輸出工具
        ///     1：數據輸出工具
        ///     5：資料輸入（無協定）工具
        ///     9999：全部工具
        /// </param>
        /// <remarks>指定工具類別或編號，清除快取。</remarks>
        public ErrorCode TBC(int m, int nnnn)
        {
            Send("TBC", $"{m},{nnnn}", out var err, out var res);
            return SetErr("TBC", err);
        }

        /// <summary>複製儲存格值</summary>
        /// <param name="sc">要複製的起點儲存格的列編號（1～100）</param>
        /// <param name="sr">要複製的起點儲存格的行編號（1～1000）</param>
        /// <param name="ec">要複製的終點單元格的列編號（1～100）</param>
        /// <param name="er">要複製的終點單元格的行編號（1～1000）</param>
        /// <param name="pc">要貼上的儲存格的列號（1～100）</param>
        /// <param name="pr">要貼上的儲存格的行編號（1～1000）</param>
        /// <remarks>指定要複製和要貼上的儲存格範圍，複製儲存格值。</remarks>
        public ErrorCode CCV(int sc, int sr, int ec, int er, int pc, int pr)
        {
            Send("CCV", $"{sc},{sr},{ec},{er},{pc},{pr}", out var err, out var res);
            return SetErr("CCV", err);
        }

        /// <summary>重新連接設備</summary>
        /// <remarks>重新連接裝置。</remarks>
        public ErrorCode DRC()
        {
            Send("DRC", out var err, out var res);
            return SetErr("DRC", err);
        }

        /// <summary>輸出資料初始化</summary>
        /// <remarks>初始化輸出資料。</remarks>
        public ErrorCode RSOD()
        {
            Send("RSOD", out var err, out var res);
            return SetErr("RSOD", err);
        }

        /// <summary>匯出儲存格值</summary>
        /// <param name="nn">文件編號（0～99）</param>
        /// <param name="sc">起點儲存格的列編號（1～100）</param>
        /// <param name="sr">起點儲存格的行編號（1～1000）</param>
        /// <param name="ec">終點單元格的列編號（1～100）</param>
        /// <param name="er">終點單元格的行編號（1～1000）</param>
        /// <remarks>指定檔案編號和儲存目標範圍，匯出儲存格值。</remarks>
        public ErrorCode CEV(int nn, int sc, int sr, int ec, int er)
        {
            Send("CEV", out var err, out var res);
            return SetErr("CEV", err);
        }

        /// <summary>導入單元格值</summary>
        /// <param name="nn">文件編號（0～99）</param>
        /// <param name="pc">匯入位置的儲存格列編號（1～100）</param>
        /// <param name="pr">匯入位置的儲存格行編號（1～1000）</param>
        /// <remarks>指定檔案編號和要匯入的儲存格的列編號/行編號，匯入儲存格值。</remarks>
        public ErrorCode CIV(int nn, int pc, int pr)
        {
            Send("CIV", out var err, out var res);
            return SetErr("CIV", err);
        }

    }

    internal static class _Extensions
    {
        /// <summary>
        /// 檢查第一個元素是否為 "ER"
        /// </summary>
        /// <param name="txt"></param>
        /// <returns></returns>
        public static bool ER(this string[] txt) => txt.Get(0).IsEquals("ER");

        public static bool ER(this string[] txt, out string err)
        {
            if (txt.ER())
                return txt.TryGetValueAt(2, out err);
            err = null;
            return false;
        }

        public static VS.ErrorCode IsSuccess(this string[] txt, string cmd)
        {
            if (txt == null) return VS.ErrorCode.ER;
            if (txt.Length == 1 && txt[0].IsEquals(cmd)) return VS.ErrorCode.Success;
            return VS.ErrorCode.ER;
        }

        public static string[] Exception = new string[] { "", "", "Exception" };
        public static string[] Busy = new string[] { "", "", "Busy" };
        public static string[] Timeout = new string[] { "", "", "Timeout" };
    }
}