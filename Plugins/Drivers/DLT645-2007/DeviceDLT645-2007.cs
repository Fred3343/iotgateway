using Microsoft.Extensions.Logging;
using PluginInterface;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Meter.Dlt645
{
    [DriverSupported("DLT645")]
    [DriverSupported("DLT645-2007")]
    [DriverSupported("DLT645-1997")]
    [DriverInfo("Dlt645", "V1.0.2", "Copyright YourCompany 202412")]
    public class DeviceDlt645 : IDriver
    {
        public ILogger _logger { get; set; }
        private readonly string _device;

        private SerialPort? _serial;
        private TcpClient? _tcpClient;
        private NetworkStream? _netStream;
        private readonly object _lock = new();

        #region DI 小数位映射（内置表）

        /// <summary>
        /// DI -> 小数位数
        /// key 为 8 位 16 进制字符串（大写、不带 0x），例如 "00030102"
        /// </summary>
        private static readonly Dictionary<string, int> DiDecimalMap = new()
        {
            // XXXXXX.XX → 2 位小数
            { "00000000", 2 },
            { "00000100", 2 },
            { "00010100", 2 },
            { "00020100", 2 },
            { "00030100", 2 },
            { "00040100", 2 },
            { "00000200", 2 },
            { "00010200", 2 },
            { "00020200", 2 },
            { "00030200", 2 },
            { "00040200", 2 },

            // XXX.X → 1 位小数
            { "00010102", 1 },
            { "00020102", 1 },
            { "00030102", 1 },

            // XXX.XXX → 3 位小数
            { "00010202", 3 },
            { "00020202", 3 },
            { "00030202", 3 },

            // XX.XXXX → 4 位小数
            { "00000302", 4 },
            { "00000402", 4 },
            { "00000502", 4 },

            // X.XXX → 3 位小数
            { "00000602", 3 },

            // 02008002 → XX.XX
            { "02008002", 2 },
            // 03008002 → XX.XXXX
            { "03008002", 4 },
            // 07008002 → XXX.X
            { "07008002", 1 },
            // 08008002 → XX.XX
            { "08008002", 2 },
        };

        /// <summary>
        /// 根据 DI 字节获取小数位数，未配置则返回 0（不缩放）
        /// </summary>
        private static int GetDecimalPlaces(byte[] diBytes)
        {
            if (diBytes == null || diBytes.Length != 4)
                return 0;

            var sb = new StringBuilder(8);
            foreach (var b in diBytes)
                sb.AppendFormat("{0:X2}", b); // 例如 00 03 01 02 → "00030102"

            var key = sb.ToString();
            return DiDecimalMap.TryGetValue(key, out var places) ? places : 0;
        }

        #endregion

        #region 配置参数

        [ConfigParameter("设备Id")]
        public string DeviceId { get; set; }

        public enum LinkTypeEnum
        {
            Serial = 0,
            Tcp = 1
        }

        [ConfigParameter("通讯方式(Serial/Tcp)")]
        public LinkTypeEnum LinkType { get; set; } = LinkTypeEnum.Serial;

        // 串口参数
        [ConfigParameter("串口名称(如 COM1 或 /dev/ttyS3)")]
        public string PortName { get; set; } = "/dev/ttyS1";

        [ConfigParameter("波特率")]
        public int BaudRate { get; set; } = 2400;

        [ConfigParameter("数据位")]
        public int DataBits { get; set; } = 8;

        [ConfigParameter("停止位(1/2)")]
        public int StopBitsCfg { get; set; } = 1;

        [ConfigParameter("校验位(None/Even/Odd)")]
        public string ParityCfg { get; set; } = "Even";

        // TCP 参数
        [ConfigParameter("IP地址")]
        public string IpAddress { get; set; } = "192.168.1.100";

        [ConfigParameter("端口号")]
        public int Port { get; set; } = 9000;

        // 公共参数
        [ConfigParameter("表地址(12位十进制或 AAAAAAAAAAAA 广播)")]
        public string MeterAddress { get; set; } = "AAAAAAAAAAAA";

        /// <summary>
        /// 2007: DI 和数据 +0x33；1997: 不加 0x33（少数老/兼容表用）
        /// </summary>
        [ConfigParameter("规约版本(2007/1997)")]
        public string ProtocolVersion { get; set; } = "2007";

        [ConfigParameter("超时时间ms")]
        public int Timeout { get; set; } = 1000;

        [ConfigParameter("最小通讯周期ms")]
        public uint MinPeriod { get; set; } = 3000;

        #endregion

        #region 生命周期

        public DeviceDlt645(string device, ILogger logger)
        {
            _device = device;
            _logger = logger;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _logger.LogInformation($"Device:[{_device}],Create()");
        }

        /// <summary>
        /// 连接状态
        /// </summary>
        public bool IsConnected
        {
            get
            {
                return LinkType switch
                {
                    LinkTypeEnum.Serial => _serial != null && _serial.IsOpen,
                    LinkTypeEnum.Tcp => _tcpClient != null && _tcpClient.Connected,
                    _ => false
                };
            }
        }

        /// <summary>
        /// 连接
        /// </summary>
        public bool Connect()
        {
            lock (_lock)
            {
                try
                {
                    _logger.LogInformation($"Device:[{_device}],Connect()");

                    CloseInternal();

                    if (LinkType == LinkTypeEnum.Serial)
                    {
                        _serial = new SerialPort
                        {
                            PortName = PortName,
                            BaudRate = BaudRate,
                            DataBits = DataBits,
                            ReadTimeout = Timeout,
                            WriteTimeout = Timeout
                        };

                        _serial.StopBits = StopBitsCfg switch
                        {
                            2 => StopBits.Two,
                            _ => StopBits.One
                        };

                        _serial.Parity = ParityCfg?.ToLower() switch
                        {
                            "none" => Parity.None,
                            "odd" => Parity.Odd,
                            _ => Parity.Even
                        };

                        _serial.Open();
                    }
                    else
                    {
                        _tcpClient = new TcpClient
                        {
                            ReceiveTimeout = Timeout,
                            SendTimeout = Timeout
                        };
                        _tcpClient.Connect(IpAddress, Port);
                        _netStream = _tcpClient.GetStream();
                        _netStream.ReadTimeout = Timeout;
                        _netStream.WriteTimeout = Timeout;
                    }

                    return IsConnected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Device:[{_device}],Connect(),Error");
                    CloseInternal();
                    return false;
                }
            }
        }

        /// <summary>
        /// 断开
        /// </summary>
        public bool Close()
        {
            lock (_lock)
            {
                try
                {
                    _logger.LogInformation($"Device:[{_device}],Close()");
                    CloseInternal();
                    return !IsConnected;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Device:[{_device}],Close(),Error");
                    return false;
                }
            }
        }

        private void CloseInternal()
        {
            try
            {
                if (_serial != null)
                {
                    _serial.Close();
                    _serial.Dispose();
                    _serial = null;
                }

                if (_netStream != null)
                {
                    _netStream.Close();
                    _netStream.Dispose();
                    _netStream = null;
                }

                if (_tcpClient != null)
                {
                    _tcpClient.Close();
                    _tcpClient.Dispose();
                    _tcpClient = null;
                }
            }
            catch
            {
                // ignore
            }
        }

        public void Dispose()
        {
            try
            {
                _logger.LogInformation($"Device:[{_device}],Dispose()");
                CloseInternal();
                GC.SuppressFinalize(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Device:[{_device}],Dispose(),Error");
            }
        }

        #endregion

        #region 读

        /// <summary>
        /// 读 DLT645 数据
        /// ioArg.Address: 8位 DI 码(16进制)，如 "00020000"、"00030102" 等
        /// 小数位根据内置 DI 表自动缩放，例如 00030102 → XXX.X → 除以 10
        /// </summary>
        [Method("读DLT645数据", description: "地址为8位DI码，如00020000、00030102等")]
        public DriverReturnValueModel Read(DriverAddressIoArgModel ioArg)
        {
            var ret = new DriverReturnValueModel
            {
                StatusType = VaribaleStatusTypeEnum.Good
            };

            try
            {
                if (!IsConnected && !Connect())
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = "连接失败";
                    return ret;
                }

                if (string.IsNullOrWhiteSpace(ioArg.Address))
                {
                    ret.StatusType = VaribaleStatusTypeEnum.AddressError;
                    ret.Message = "地址(DI)为空";
                    return ret;
                }

                // 解析 DI
                var diBytes = ParseDi(ioArg.Address!.Trim());
                if (diBytes == null)
                {
                    ret.StatusType = VaribaleStatusTypeEnum.AddressError;
                    ret.Message = "DI格式错误，需8位16进制，如00020000";
                    return ret;
                }

                // 解析表地址
                var addrBytes = ParseAddress(MeterAddress);
                if (addrBytes == null)
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = "表地址格式错误，应为12位十进制字符串或 AAAAAAAAAAAA(广播)";
                    return ret;
                }

                bool useOffset33 = ProtocolVersion?.Trim() == "2007";

                // 645-2007 读数据控制码：0x11；异常应答为 0xD1
                byte ctrl = useOffset33 ? (byte)0x11 : (byte)0x01;

                var frame = BuildReadFrame(addrBytes, ctrl, diBytes, useOffset33);

                byte[]? resp;
                lock (_lock)
                {
                    resp = SendAndReceive(frame);
                }

                if (resp == null || resp.Length < 12)
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = "未收到有效响应";
                    return ret;
                }

                if (!TryParseResponse(resp, addrBytes, diBytes, useOffset33,
                        out var respCtrl, out var decodedData, out var err))
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = err;
                    return ret;
                }

                // 异常应答：0xD1
                if ((respCtrl & 0xF0) == 0xD0)
                {
                    byte errCode = decodedData.Length > 0 ? decodedData[0] : (byte)0xFF;
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = $"电表返回异常应答: Ctrl=0x{respCtrl:X2}, ErrCode=0x{errCode:X2}";
                    return ret;
                }

                if (decodedData.Length <= 4)
                {
                    ret.StatusType = VaribaleStatusTypeEnum.Bad;
                    ret.Message = $"电表应答数据区过短(len={decodedData.Length})";
                    return ret;
                }

                // 前4字节是 DI，后面是数据
                var respDi = decodedData.Take(4).ToArray();
                var dataBytes = decodedData.Skip(4).ToArray();

                string diKey = BitConverter.ToString(respDi).Replace("-", "");
                _logger?.LogInformation(
                    $"Device:[{_device}] DLT645 DI={diKey} RawData={BitConverter.ToString(dataBytes).Replace("-", " ")}");

                // 如果需要严格校验 DI 一致性，这里可以比对
                if (!respDi.SequenceEqual(diBytes))
                {
                    _logger?.LogWarning(
                        $"Device:[{_device}] DLT645 DI不一致: 请求={BitConverter.ToString(diBytes)} 响应={BitConverter.ToString(respDi)}");
                }

                // 字符串类型：按编码直接返回
                if (ioArg.ValueType == DataTypeEnum.AsciiString
                    || ioArg.ValueType == DataTypeEnum.Utf8String
                    || ioArg.ValueType == DataTypeEnum.Gb2312String)
                {
                    ret.Value = DecodeString(ioArg.ValueType, dataBytes);
                    return ret;
                }

                // 数值类型：先 BCD → 整数（double），再按 DI 小数位缩放
                double rawNumber = BcdBytesToDouble(dataBytes);
                int decimalPlaces = GetDecimalPlaces(respDi);
                double scaledNumber = rawNumber;

                if (decimalPlaces > 0)
                {
                    scaledNumber = rawNumber / Math.Pow(10, decimalPlaces);
                }

                _logger?.LogInformation(
                    $"Device:[{_device}] DLT645 数值解码: DI={diKey} 原始={rawNumber} 小数位={decimalPlaces} 最终={scaledNumber}");

                switch (ioArg.ValueType)
                {
                    case DataTypeEnum.Float:
                        ret.Value = (float)scaledNumber;
                        break;
                    case DataTypeEnum.Int16:
                        ret.Value = (short)scaledNumber;
                        break;
                    case DataTypeEnum.Int32:
                        ret.Value = (int)scaledNumber;
                        break;
                    case DataTypeEnum.Uint16:
                        ret.Value = (ushort)Math.Max(0, scaledNumber);
                        break;
                    case DataTypeEnum.Uint32:
                        ret.Value = (uint)Math.Max(0, scaledNumber);
                        break;
                    case DataTypeEnum.Double:
                    case DataTypeEnum.Int64:
                        ret.Value = scaledNumber;
                        break;
                    default:
                        // 未知类型就直接返回 double
                        ret.Value = scaledNumber;
                        break;
                }
            }
            catch (Exception ex)
            {
                ret.StatusType = VaribaleStatusTypeEnum.Bad;
                ret.Message = $"读取失败,{ex.Message}";
            }

            return ret;
        }

        #endregion

        #region 写(预留)

        public async Task<RpcResponse> WriteAsync(string requestId, string method, DriverAddressIoArgModel ioArg)
        {
            RpcResponse rpcResponse = new() { IsSuccess = false };

            try
            {
                if (!IsConnected)
                {
                    rpcResponse.Description = "设备连接已断开";
                    return rpcResponse;
                }

                // 如需实现拉闸/合闸/参数设置，可在这里根据 method + ioArg 组装写帧
                rpcResponse.Description = "DLT645写入未实现，如需写表请扩展驱动";
            }
            catch (Exception ex)
            {
                rpcResponse.Description = $"写入失败,[method]:{method},[ioArg]:{ioArg},[ex]:{ex}";
            }

            return await Task.FromResult(rpcResponse);
        }

        #endregion

        #region 底层通讯 + 帧处理

        private byte[]? SendAndReceive(byte[] frame)
        {
            if (!IsConnected)
                return null;

            byte[] wakeup = new byte[] { 0xFE, 0xFE, 0xFE, 0xFE };

            if (LinkType == LinkTypeEnum.Serial)
            {
                if (_serial == null || !_serial.IsOpen)
                    return null;

                try
                {
                    _serial.DiscardInBuffer();
                    var sendBytes = wakeup.Concat(frame).ToArray();
                    _serial.Write(sendBytes, 0, sendBytes.Length);
                    _logger?.LogInformation($"Device:[{_device}] DLT645 Send: {ToHex(sendBytes)}");

                    var buf = new List<byte>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while (sw.ElapsedMilliseconds < Timeout + 200)
                    {
                        int avail = _serial.BytesToRead;
                        if (avail > 0)
                        {
                            var tmp = new byte[avail];
                            int read = _serial.Read(tmp, 0, tmp.Length);
                            if (read > 0)
                            {
                                buf.AddRange(tmp.AsSpan(0, read).ToArray());
                                if (buf.Contains((byte)0x16))
                                    break;
                            }
                        }

                        Thread.Sleep(10);
                    }

                    if (buf.Count == 0)
                    {
                        _logger?.LogWarning($"Device:[{_device}] DLT645 无任何返回");
                        return null;
                    }

                    var resp = buf.ToArray();
                    _logger?.LogInformation($"Device:[{_device}] DLT645 Recv({resp.Length}): {ToHex(resp)}");
                    return resp;
                }
                catch (TimeoutException)
                {
                    _logger?.LogWarning($"Device:[{_device}] DLT645 读取超时");
                    return null;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Device:[{_device}] DLT645 通讯异常");
                    return null;
                }
            }
            else // TCP
            {
                if (_netStream == null || !_netStream.CanWrite)
                    return null;

                try
                {
                    var sendBytes = wakeup.Concat(frame).ToArray();
                    _netStream.Write(sendBytes, 0, sendBytes.Length);
                    _netStream.Flush();
                    _logger?.LogInformation($"Device:[{_device}] DLT645(TCP) Send: {ToHex(sendBytes)}");

                    var buf = new List<byte>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while (sw.ElapsedMilliseconds < Timeout + 200)
                    {
                        if (_tcpClient != null && !_tcpClient.Connected)
                            break;

                        if (_netStream.DataAvailable)
                        {
                            var tmp = new byte[256];
                            int read = _netStream.Read(tmp, 0, tmp.Length);
                            if (read > 0)
                            {
                                buf.AddRange(tmp.AsSpan(0, read).ToArray());
                                if (buf.Contains((byte)0x16))
                                    break;
                            }
                        }

                        Thread.Sleep(10);
                    }

                    if (buf.Count == 0)
                    {
                        _logger?.LogWarning($"Device:[{_device}] DLT645(TCP) 无任何返回");
                        return null;
                    }

                    var resp = buf.ToArray();
                    _logger?.LogInformation($"Device:[{_device}] DLT645(TCP) Recv({resp.Length}): {ToHex(resp)}");
                    return resp;
                }
                catch (TimeoutException)
                {
                    _logger?.LogWarning($"Device:[{_device}] DLT645(TCP) 读取超时");
                    return null;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Device:[{_device}] DLT645(TCP) 通讯异常");
                    return null;
                }
            }
        }

        /// <summary>
        /// 构造读数据帧: 68 A0..A5 68 C L (DI [+0x33]...) CS 16
        /// </summary>
        private static byte[] BuildReadFrame(byte[] addr, byte ctrl, byte[] di, bool useOffset33)
        {
            if (addr.Length != 6)
                throw new ArgumentException("addr length must be 6");
            if (di.Length != 4)
                throw new ArgumentException("di length must be 4");

            var frame = new List<byte>();
            frame.Add(0x68);
            frame.AddRange(addr);
            frame.Add(0x68);
            frame.Add(ctrl);

            byte[] diBytes = useOffset33
                ? di.Select(b => (byte)(b + 0x33)).ToArray()
                : di.ToArray();

            byte len = (byte)diBytes.Length;
            frame.Add(len);
            frame.AddRange(diBytes);

            byte cs = 0;
            for (int i = 0; i < frame.Count; i++)
                cs += frame[i];

            frame.Add(cs);
            frame.Add(0x16);
            return frame.ToArray();
        }

        /// <summary>
        /// 解析响应帧，返回“解码后”的数据区(包含DI+数据)。
        /// useOffset33 = true 时，对数据区每个字节做 -0x33
        /// </summary>
        private bool TryParseResponse(
            byte[] resp,
            byte[] expectedAddr,
            byte[] requestDi,
            bool useOffset33,
            out byte ctrl,
            out byte[] decodedData,
            out string err)
        {
            decodedData = Array.Empty<byte>();
            err = string.Empty;
            ctrl = 0;

            try
            {
                int start = Array.IndexOf(resp, (byte)0x68);
                if (start < 0 || resp.Length < start + 12)
                {
                    err = "响应长度不足或无起始符";
                    return false;
                }

                if (resp[start + 7] != 0x68)
                {
                    err = "帧格式错误(第二个0x68不匹配)";
                    return false;
                }

                // 地址校验（可选）
                var addr = new byte[6];
                Array.Copy(resp, start + 1, addr, 0, 6);
                if (!addr.SequenceEqual(expectedAddr))
                {
                    _logger?.LogWarning(
                        $"Device:[{_device}] DLT645 响应地址与请求不一致: Req={ToHex(expectedAddr)} Resp={ToHex(addr)}");
                }

                ctrl = resp[start + 8];
                byte len = resp[start + 9];
                int dataStart = start + 10;
                int dataEnd = dataStart + len; // 不含

                if (resp.Length < dataEnd + 2) // +CS +16
                {
                    err = "响应长度与L不匹配";
                    return false;
                }

                // 校验和
                byte cs = 0;
                for (int i = start; i < dataEnd; i++)
                    cs += resp[i];

                if (cs != resp[dataEnd])
                {
                    err = "校验和错误";
                    return false;
                }

                if (resp[dataEnd + 1] != 0x16)
                {
                    err = "结束符错误";
                    return false;
                }

                var data = new byte[len];
                Array.Copy(resp, dataStart, data, 0, len);

                if (useOffset33)
                {
                    for (int i = 0; i < data.Length; i++)
                        data[i] = (byte)(data[i] - 0x33);
                }

                decodedData = data;
                return true;
            }
            catch (Exception ex)
            {
                err = $"解析异常: {ex.Message}";
                return false;
            }
        }

        #endregion

        #region 辅助函数：地址/DI/BCD/字符串

        /// <summary>
        /// 表地址: 12位十进制 -> 6字节BCD(低字节在前)
        /// "043307551996" -> 04 33 07 55 19 96 -> 发帧时按 96 19 55 07 33 04
        /// "AAAAAAAAAAAA" 或空: 广播地址 AA AA AA AA AA AA
        /// </summary>
        private static byte[]? ParseAddress(string addr)
        {
            if (string.IsNullOrWhiteSpace(addr))
                return null;

            addr = addr.Trim();
            if (addr.Equals("AAAAAAAAAAAA", StringComparison.OrdinalIgnoreCase))
                return Enumerable.Repeat((byte)0xAA, 6).ToArray();

            var digits = new string(addr.Where(char.IsDigit).ToArray());
            if (digits.Length != 12)
                return null;

            var bytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                int hi = digits[i * 2] - '0';
                int lo = digits[i * 2 + 1] - '0';
                if (hi < 0 || hi > 9 || lo < 0 || lo > 9)
                    return null;
                bytes[5 - i] = (byte)((hi << 4) | lo);
            }

            return bytes;
        }

        /// <summary>
        /// DI: 8位16进制字符串 -> 4字节
        /// 例："00030102" -> {0x00,0x03,0x01,0x02}
        /// </summary>
        private static byte[]? ParseDi(string diStr)
        {
            if (string.IsNullOrWhiteSpace(diStr))
                return null;

            diStr = diStr.Replace(" ", "").Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (diStr.Length != 8)
                return null;

            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                if (!byte.TryParse(diStr.Substring(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber,
                    null, out var b))
                    return null;
                bytes[i] = b;
            }

            return bytes;
        }

        /// <summary>
        /// BCD字节数组 -> 整数(double)，从高字节到低字节拼十进制数
        /// 小数点不在此处处理，由 DI 表决定缩放
        /// </summary>
        private static double BcdBytesToDouble(byte[] data)
        {
            if (data == null || data.Length == 0)
                return 0;

            var sb = new StringBuilder();

            // 一般低字节存最低位，这里从高到低拼接
            for (int i = data.Length - 1; i >= 0; i--)
            {
                byte b = data[i];
                int hi = (b >> 4) & 0x0F;
                int lo = b & 0x0F;

                if (hi <= 9)
                    sb.Append(hi);
                if (lo <= 9)
                    sb.Append(lo);
            }

            if (sb.Length == 0)
                return 0;

            if (double.TryParse(sb.ToString(), out var val))
                return val;

            return 0;
        }

        private static string DecodeString(DataTypeEnum type, byte[] data)
        {
            try
            {
                var bytes = data.ToArray();

                return type switch
                {
                    DataTypeEnum.Utf8String =>
                        Encoding.UTF8.GetString(bytes).TrimEnd('\0'),

                    DataTypeEnum.Gb2312String =>
                        Encoding.GetEncoding("gb2312")
                            .GetString(bytes).TrimEnd('\0'),

                    DataTypeEnum.AsciiString =>
                        Encoding.ASCII.GetString(
                            bytes.Where(x => x is >= 0x20 and <= 0x7E).ToArray()
                        ).TrimEnd('\0'),

                    _ => Encoding.ASCII.GetString(bytes).TrimEnd('\0')
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            return BitConverter.ToString(data).Replace("-", " ");
        }

        #endregion
    }
}
