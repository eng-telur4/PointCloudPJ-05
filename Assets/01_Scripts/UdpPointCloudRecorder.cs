using UnityEngine;
using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class UdpPointCloudRecorder : MonoBehaviour
{
    [Header("UDP Settings")]
    public int listenPort = 5000;
    
    [Header("Dependencies")]
    public RendererBase renderer;

    [Header("Controls")]
    [Tooltip("チェックを入れると画面の更新が止まります（裏での受信・録画は継続します）")]
    public bool isPaused = false;

    // --- 録画用の状態管理 ---
    public bool IsRecording => _isRecording;
    public int RecordedFrameCount => _recordedFrameCount;
    public int CurrentLiveFrameCount => _currentLiveFrameCount;

    private bool _isRecording = false;
    private int _recordedFrameCount = 0;
    private int _currentLiveFrameCount = 0;
    private FileStream _recordFileStream;
    private BinaryWriter _recordWriter;
    private string _currentRecordPath;

    private PointInfo[] _currentPoints; // 画面の一時停止＆単一フレーム保存用

    // --- UDP定数とバッファ ---
    private const int CHUNK_SIZE = 4096;
    private const int HEADER_SIZE = 20;
    private const int BYTES_PER_POINT = 18; 
    private const int FLOAT_SIZE = 4;

    private Dictionary<uint, byte[]> _frameBuffers = new Dictionary<uint, byte[]>();
    private Dictionary<uint, int> _receivedChunkCounts = new Dictionary<uint, int>();
    private uint _lastProcessedFrameId = 0;
    
    private Queue<PointInfo[]> _dataQueue = new Queue<PointInfo[]>();
    private UdpClient _udpClient;
    private CancellationTokenSource _cts;

    void Start()
    {
        if (renderer == null)
        {
            Debug.LogError("[Recorder] Renderer is not assigned.");
            enabled = false;
            return;
        }

        _cts = new CancellationTokenSource();
        Task.Run(() => StartReceiving(_cts.Token)); 
    }

    void Update()
    {
        if (_dataQueue.Count > 0)
        {
            PointInfo[] newPositions;
            lock (_dataQueue)
            {
                newPositions = _dataQueue.Dequeue();
            }

            _currentLiveFrameCount++;
            _currentPoints = newPositions; // 最新フレームを保持

            // 録画中ならバイナリファイルへ追記
            if (_isRecording && _recordWriter != null)
            {
                WriteFrameToStream(_recordWriter, newPositions);
                _recordedFrameCount++;
            }

            // ポーズ中でなければ画面を更新
            if (!isPaused)
            {
                PointInfoContainer.ReplacePositions(newPositions);
                renderer.SetDataDirty();
            }
        }
    }

    // ----------------------------------------------------------
    // ファイル保存・録画ロジック (冗長性を避けるため共通関数化)
    // ----------------------------------------------------------

    /// <summary>
    /// PointInfo配列をPlayerが読み込めるバイナリ形式に変換してストリームに書き込む
    /// </summary>
    private void WriteFrameToStream(BinaryWriter writer, PointInfo[] points)
    {
        int numPoints = points.Length;
        int dataLen = numPoints * BYTES_PER_POINT;
        double timestamp = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

        // Player側の読み込み形式: dataLen(4) -> numPoints(4) -> timestamp(8)
        writer.Write(dataLen);
        writer.Write(numPoints);
        writer.Write(timestamp);

        foreach (var p in points)
        {
            writer.Write(p.position.x); // ROS X (Unity Xに格納されているためそのまま)
            writer.Write(p.position.z); // ROS Y (Unity Zに格納されているためそのまま)
            writer.Write(p.position.y); // ROS Z (Unity Yに格納されているためそのまま)
            writer.Write(p.intensity);
            writer.Write((byte)p.tag);
            writer.Write((byte)p.line);
        }
    }

    public void SaveSingleFrame()
    {
        if (_currentPoints == null || _currentPoints.Length == 0)
        {
            Debug.LogWarning("[Recorder] No point cloud data received yet.");
            return;
        }

        string fileName = $"single_frame_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
        string path = Path.Combine(Directory.GetParent(Application.dataPath).FullName + "\\Assets\\StreamingAssets\\", fileName);

        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                WriteFrameToStream(writer, _currentPoints);
            }
            Debug.Log($"<b>[Single Save]</b> Saved to: {path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Single Save] Error: {e.Message}");
        }
    }

    public void StartContinuousRecording()
    {
        if (_isRecording) return;

        string fileName = $"continuous_record_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
        _currentRecordPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName + "\\Assets\\StreamingAssets\\", fileName);

        try
        {
            _recordFileStream = new FileStream(_currentRecordPath, FileMode.Create, FileAccess.Write);
            _recordWriter = new BinaryWriter(_recordFileStream);
            _isRecording = true;
            _recordedFrameCount = 0;
            Debug.Log($"<color=red><b>[Recording Started]</b></color> File: {_currentRecordPath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Recording] Failed to start: {e.Message}");
        }
    }

    public void StopContinuousRecording()
    {
        if (!_isRecording) return;

        _isRecording = false;
        if (_recordWriter != null)
        {
            _recordWriter.Close();
            _recordFileStream.Close();
            _recordWriter = null;
            _recordFileStream = null;
        }
        
        Debug.Log($"<color=blue><b>[Recording Stopped]</b></color> Saved {_recordedFrameCount} frames to: {_currentRecordPath}");
    }

    void OnDestroy()
    {
        StopContinuousRecording();
        _cts?.Cancel();
        _udpClient?.Close();
    }

    // ----------------------------------------------------------
    // UDP受信ロジック (変更なし)
    // ----------------------------------------------------------
    private async Task StartReceiving(CancellationToken token)
    {
        try
        {
            using (_udpClient = new UdpClient(listenPort)) 
            {
                _udpClient.Client.ReceiveBufferSize = 8 * 1024 * 1024;
                while (!token.IsCancellationRequested)
                {
                    IPEndPoint clientEndPoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] receivedPacket = _udpClient.Receive(ref clientEndPoint);
                    
                    ProcessChunkPacket(receivedPacket);
                    CleanUpOldFrames();
                }
            }
        }
        catch (SocketException e) when (e.ErrorCode == 10004) { /* Cancelled */ }
        catch (Exception e) { Debug.LogError($"UDP Error: {e.Message}"); }
    }

    private void ProcessChunkPacket(byte[] packet)
    {
        if (packet.Length < HEADER_SIZE) return;

        UdpHeader header;
        GCHandle handle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try { header = (UdpHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(UdpHeader)); }
        finally { if (handle.IsAllocated) handle.Free(); }

        if (header.frame_id <= _lastProcessedFrameId) return;
        if (header.data_size > CHUNK_SIZE) return;

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

        if (_receivedChunkCounts.ContainsKey(header.frame_id) && _receivedChunkCounts[header.frame_id] == header.num_chunks)
        {
            byte[] completeFrame = _frameBuffers[header.frame_id];
            PointInfo[] pointInfos = ConvertToVector3Array(completeFrame);

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
        if (data.Length % BYTES_PER_POINT != 0) return new PointInfo[0];

        int numPoints = data.Length / BYTES_PER_POINT;
        PointInfo[] pointInfos = new PointInfo[numPoints];
        
        for (int i = 0; i < numPoints; i++)
        {
            int byteIndex = i * BYTES_PER_POINT;
            pointInfos[i].position.x = BitConverter.ToSingle(data, byteIndex + 0);
            pointInfos[i].position.z = BitConverter.ToSingle(data, byteIndex + FLOAT_SIZE);
            pointInfos[i].position.y = BitConverter.ToSingle(data, byteIndex + (2 * FLOAT_SIZE));
            pointInfos[i].intensity = BitConverter.ToSingle(data, byteIndex + (3 * FLOAT_SIZE));
            
            // バイトデータのタグ・ラインの取得方法を修正
            pointInfos[i].tag = (char)data[byteIndex + 16];
            pointInfos[i].line = (char)data[byteIndex + 17];
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
                if (kvp.Key < threshold) keysToRemove.Add(kvp.Key);
            }

            foreach (uint key in keysToRemove)
            {
                _frameBuffers.Remove(key);
                _receivedChunkCounts.Remove(key);
            }
        }
    }
}

// ==========================================================
// InspectorのカスタムUI (ボタンの配置)
// ==========================================================
#if UNITY_EDITOR
[CustomEditor(typeof(UdpPointCloudRecorder))]
public class UdpPointCloudRecorderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        UdpPointCloudRecorder recorder = (UdpPointCloudRecorder)target;

        GUILayout.Space(15);
        GUILayout.Label("Live Stream Info", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Received Live Frames", recorder.CurrentLiveFrameCount.ToString());

        GUILayout.Space(15);
        GUILayout.Label("Single Frame Save", EditorStyles.boldLabel);
        
        if (GUILayout.Button("Save Current Frame (.bin)", GUILayout.Height(30)))
        {
            recorder.SaveSingleFrame();
        }

        GUILayout.Space(15);
        GUILayout.Label("Continuous Recording", EditorStyles.boldLabel);
        
        if (recorder.IsRecording)
        {
            EditorGUILayout.HelpBox($"Recording in progress... Frames saved: {recorder.RecordedFrameCount}", MessageType.Info);
            
            GUI.backgroundColor = Color.red;
            if (GUILayout.Button("Stop & Save Recording", GUILayout.Height(40)))
            {
                recorder.StopContinuousRecording();
            }
            GUI.backgroundColor = Color.white;
        }
        else
        {
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("Start Recording", GUILayout.Height(40)))
            {
                recorder.StartContinuousRecording();
            }
            GUI.backgroundColor = Color.white;
        }
    }
    
    // インスペクターの値をリアルタイム更新する
    public override bool RequiresConstantRepaint()
    {
        return Application.isPlaying;
    }
}
#endif