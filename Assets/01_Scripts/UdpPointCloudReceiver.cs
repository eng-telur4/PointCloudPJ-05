using UnityEngine;
using System;
using System.IO; // 追加
using System.Text; // 追加
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public struct PointInfo
{
    public Vector3 position;    // 座標(x,y,z)
    public float intensity;     // 反射強度
    public char tag;            // タグ情報
    public char line;           // 識別情報
}

public struct BoxInfo
{
    public Vector3 position;    // 座標(x,y,z)
    public Vector3 size;        // サイズ(dx,dy,dz)
    public float heading;       // 向き(角度)
    public string label;        // ラベル

}

public struct AllDataInfo
{
    public PointInfo pointInfo;
    public BoxInfo boxInfo;
}

// C++のヘッダー構造体と厳密に一致させるための定義 (20バイト)
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UdpHeader
{
    public UInt32 frame_id;
    public UInt32 total_size;
    public UInt32 chunk_index;
    public UInt32 num_chunks;
    public UInt32 data_size;
}

public class UdpPointCloudReceiver : MonoBehaviour
{
    // ----------------------------------------------------------
    // 設定項目.
    [Header("UDP Settings")]
    public int listenPort = 5000;
    
    [Header("Dependencies")]
    public RendererBase renderer;

    // ----------------------------------------------------------
    // UDP/チャンク定数 (C++側と一致)
    private const int CHUNK_SIZE = 4096;
    private const int HEADER_SIZE = 20;
    private const int BYTES_PER_POINT = 18; 
    private const int FLOAT_SIZE = 4;

    // フレーム再構築用バッファ
    private Dictionary<uint, byte[]> _frameBuffers = new Dictionary<uint, byte[]>();
    private Dictionary<uint, int> _receivedChunkCounts = new Dictionary<uint, int>();
    private uint _lastProcessedFrameId = 0;
    
    // データ受け渡し用キューとタスク管理
    private Queue<PointInfo[]> _dataQueue = new Queue<PointInfo[]>();
    private UdpClient _udpClient;
    private CancellationTokenSource _cts;

    // --- 【ログ保存用変数】 ---
    private string _logFileName = null;
    private const string FILE_PATH = "PointDataLogs";
    private const string TIME_FORMAT_FILE = "yyyyMMdd_HHmmss"; 
    private bool _headerWritten = false;
    
    // 上限フレーム数
    private const int MAX_LOG_COUNT = 5000;
    private int _loggedFrameCount = 0;
    // -------------------------

    // ----------------------------------------------------------
    // ライフサイクル.

    void Start()
    {
        if (renderer == null)
        {
            Debug.LogError("PointCloudRenderer is not assigned. Cannot start receiver.");
            enabled = false;
            return;
        }

        Debug.Log($"[UDP Receiver] Logging target: {MAX_LOG_COUNT} frames.");

        _cts = new CancellationTokenSource();
        // UDP受信を非同期タスクで開始
        Task.Run(() => StartReceiving(_cts.Token)); 
    }

    void Update()
    {
        // メインスレッドでキューからデータを処理し、描画をトリガー
        if (_dataQueue.Count > 0)
        {
            PointInfo[] newPositions;
            
            lock (_dataQueue)
            {
                newPositions = _dataQueue.Dequeue();
            }

            // データコンテナとレンダラーを更新
            PointInfoContainer.ReplacePositions(newPositions);
            renderer.SetDataDirty();
        }
    }

    void OnDestroy()
    {
        _cts?.Cancel();
        _udpClient?.Close();
    }

    // ----------------------------------------------------------
    // UDP受信とチャンク処理 (バックグラウンドスレッド)

    private async Task StartReceiving(CancellationToken token)
    {
        try
        {
            // using ブロックを使用して UdpClient のリソースを確実に解放
            using (_udpClient = new UdpClient(listenPort)) 
            {
                _udpClient.Client.ReceiveBufferSize = 8 * 1024 * 1024;
                while (!token.IsCancellationRequested)
                {
                    IPEndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                    // Receive は同期ブロッキングメソッド
                    byte[] receivedPacket = _udpClient.Receive(ref clientEndPoint);
                    
                    // 受信したチャンクを処理
                    ProcessChunkPacket(receivedPacket);
                    CleanUpOldFrames();
                }
            }
        }
        catch (SocketException e) when (e.ErrorCode == 10004)
        {
            // 正常な終了（ソケットが閉じられたことによるキャンセル）
        }
        catch (Exception e)
        {
            Debug.LogError($"UDP General Error: {e.Message}");
        }
    }

    private void ProcessChunkPacket(byte[] packet)
    {
        if (packet.Length < HEADER_SIZE) return;

        // 1. ヘッダー情報の抽出
        UdpHeader header;
        GCHandle handle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try
        {
            header = (UdpHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(UdpHeader));
        }
        finally
        {
            if (handle.IsAllocated) handle.Free();
        }

        if (header.frame_id <= _lastProcessedFrameId) return;
        if (header.data_size > CHUNK_SIZE) return;

        // 2. フレームバッファの初期化/結合
        if (!_frameBuffers.ContainsKey(header.frame_id))
        {
            _frameBuffers[header.frame_id] = new byte[header.total_size];
            _receivedChunkCounts[header.frame_id] = 0;
        }

        int dataStart = HEADER_SIZE;
        int destinationIndex = (int)(header.chunk_index * CHUNK_SIZE);
        int copyLength = (int)header.data_size;

        if (destinationIndex + copyLength <= header.total_size)
        {
            Buffer.BlockCopy(packet, dataStart, _frameBuffers[header.frame_id], destinationIndex, copyLength);
            _receivedChunkCounts[header.frame_id]++;
        } 

        // 3. フレーム完了の確認と変換
        if (_receivedChunkCounts.ContainsKey(header.frame_id) && _receivedChunkCounts[header.frame_id] == header.num_chunks)
        {
            byte[] completeFrame = _frameBuffers[header.frame_id];
            PointInfo[] pointInfos = ConvertToVector3Array(completeFrame);

            // --- 【追加】ログ保存処理 ---
            // ネットワーク受信完了＆復元完了のタイミングで記録
            if (_loggedFrameCount < MAX_LOG_COUNT)
            {
                SaveLog(pointInfos.Length);
                _loggedFrameCount++;

                if (_loggedFrameCount == MAX_LOG_COUNT)
                {
                    Debug.Log($"[UDP Receiver] Logging COMPLETED: {MAX_LOG_COUNT} frames recorded.");
                }
            }
            // -------------------------

            // 変換後のデータをメインスレッドキューへ追加
            lock (_dataQueue) 
            {
                _dataQueue.Enqueue(pointInfos);
            }
            
            _lastProcessedFrameId = header.frame_id;
            _frameBuffers.Remove(header.frame_id);
            _receivedChunkCounts.Remove(header.frame_id);
        }
    }
    
    private PointInfo[] ConvertToVector3Array(byte[] data)
    {
        if (data.Length % BYTES_PER_POINT != 0)
        {
            Debug.LogError($"Final data size {data.Length} is corrupt. Expected multiple of {BYTES_PER_POINT}.");
            return new PointInfo[0];
        }

        int numPoints = data.Length / BYTES_PER_POINT;
        PointInfo[] pointInfos = new PointInfo[numPoints];
        
        for (int i = 0; i < numPoints; i++)
        {
            int byteIndex = i * BYTES_PER_POINT;

            // X, Y, Zは最初の12バイトに格納されていると仮定し、ROS-Unity座標変換を適用
            // ROS Z(Up) -> Unity Y(Up), ROS Y -> Unity Z
            
            // X (ROS X) -> Unity X
            pointInfos[i].position.x = BitConverter.ToSingle(data, byteIndex + 0);
            
            // Y (ROS Y) -> Unity Z
            pointInfos[i].position.z = BitConverter.ToSingle(data, byteIndex + FLOAT_SIZE);

            // Z (ROS Z) -> Unity Y
            pointInfos[i].position.y = BitConverter.ToSingle(data, byteIndex + (2 * FLOAT_SIZE));

            // Intensity
            pointInfos[i].intensity = BitConverter.ToSingle(data, byteIndex + (3 * FLOAT_SIZE));

            // Tag
            pointInfos[i].tag = (char) BitConverter.ToSingle(data, byteIndex + (3 * FLOAT_SIZE) + 1);

            // Line
            pointInfos[i].tag = (char) BitConverter.ToSingle(data, byteIndex + (3 * FLOAT_SIZE) + 2);
        }

        return pointInfos;
    }

    private void CleanUpOldFrames()
    {
        if (_lastProcessedFrameId > 0)
        {
            List<uint> keysToRemove = new List<uint>();
            uint threshold = _lastProcessedFrameId > 5 ? _lastProcessedFrameId - 5 : 0;
            
            foreach (var kvp in _frameBuffers)
            {
                if (kvp.Key < threshold)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (uint key in keysToRemove)
            {
                _frameBuffers.Remove(key);
                _receivedChunkCounts.Remove(key);
            }
        }
    }

    // ----------------------------------------------------------
    // 【追加】ログ保存メソッド
    // 形式: 点群数, UNIX時刻(秒.ナノ秒)
    // ----------------------------------------------------------
    private void SaveLog(int pointCount)
    {
        // バックグラウンドスレッドから呼ばれるため、例外処理を確実に行う
        try
        {
            // 1. ファイル名決定 (初回のみ)
            if (_logFileName == null)
            {
                string timestamp = DateTime.Now.ToString(TIME_FORMAT_FILE);
                // Renderer側の e2elog_ と区別するため udp_log_ としています
                _logFileName = $"udplog_{timestamp}.csv";
                
                // プロジェクトルート/PointDataLogs に保存
                string fullPath = Path.Combine(Application.dataPath + "/..", FILE_PATH);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
                _logFileName = Path.Combine(fullPath, _logFileName);
                
                Debug.Log($"[UDP Receiver] Started writing to: {_logFileName}");
            }

            // 2. 現在時刻を UNIXタイムスタンプ (秒単位, 小数点以下含む) で取得
            // Ubuntu側 (C++) の timebase * 1.0e-9 と形式を一致させます
            TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double unixTimestamp = t.TotalSeconds;
            
            // 3. 書き込み
            // スレッドセーフにするため、都度オープン・クローズ（実験用なのでパフォーマンス許容）
            using (StreamWriter sw = new StreamWriter(_logFileName, true, Encoding.UTF8)) 
            {
                // ヘッダー書き込み (初回のみ)
                if (!_headerWritten)
                {
                    sw.WriteLine("Point_Count,System_Time_Unix");
                    _headerWritten = true;
                }

                // 形式: 点群数, UNIX時刻(F9 = 小数点9桁)
                sw.WriteLine($"{pointCount},{unixTimestamp:F9}");
            }
        }
        catch (Exception e)
        {
            // メイン処理を止めないよう、ログだけ出して握りつぶす
            Debug.LogError($"[UDP Receiver] Failed to write log file: {e.Message}");
        }
    }
}