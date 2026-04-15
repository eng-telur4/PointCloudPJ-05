using UnityEngine;
using System;
using System.IO;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class SingleFilePointCloudPlayer : MonoBehaviour
{
    [Header("File Settings")]
    public string fileName = "all_frames.bin"; 
    public float frameRate = 10.0f;
    public bool loop = true;

    [Header("Controls")]
    public bool isPaused = false;

    [Header("Dependencies")]
    public RendererBase renderer; 

    // --- プロパティ (Inspector表示用) ---
    public int CurrentFrameCount => _currentFrameCount;

    // --- 定数 ---
    private const int BYTES_PER_POINT = 18;

    // --- 内部変数 ---
    private FileStream _fs;
    private BinaryReader _reader;
    private float _timer = 0;
    private bool _isPlaying = false;
    private string _fullPath;
    
    private PointInfo[] _currentPoints; 
    private int _currentFrameCount = 0;

    void Start()
    {
        if (renderer == null)
        {
            Debug.LogError("[SinglePlayer] Renderer not assigned.");
            enabled = false;
            return;
        }

        _fullPath = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(_fullPath))
        {
            Debug.LogError($"[SinglePlayer] File not found: {_fullPath}");
            return;
        }

        try 
        {
            _fs = new FileStream(_fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _reader = new BinaryReader(_fs);
            _isPlaying = true;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SinglePlayer] Failed to open file: {e.Message}");
        }
    }

    void Update()
    {
        if (!_isPlaying || _reader == null) return;

        // 一時停止中は自動進行しない
        if (isPaused) return;

        _timer += Time.deltaTime;
        if (_timer >= 1.0f / frameRate)
        {
            _timer = 0;
            ReadNextFrame();
        }
    }

    private void ReadNextFrame()
    {
        try
        {
            // EOFチェック
            if (_fs.Position >= _fs.Length)
            {
                if (loop)
                {
                    _fs.Seek(0, SeekOrigin.Begin);
                    _currentFrameCount = 0;
                }
                else
                {
                    _isPlaying = false;
                    return;
                }
            }

            // --- ヘッダー読み込み ---
            int dataLen = _reader.ReadInt32();
            int numPoints = _reader.ReadInt32();
            double timestamp = _reader.ReadDouble();

            // --- データ本体読み込み ---
            byte[] frameData = _reader.ReadBytes(dataLen);

            if (frameData.Length != dataLen)
            {
                if(loop) 
                {
                    _fs.Seek(0, SeekOrigin.Begin);
                    _currentFrameCount = 0;
                }
                return;
            }

            // --- 解析と適用 ---
            _currentPoints = ParseFrameData(frameData, numPoints);
            
            // 描画更新
            PointInfoContainer.ReplacePositions(_currentPoints);
            renderer.SetDataDirty();
            
            _currentFrameCount++;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SinglePlayer] Error: {e.Message}");
            _isPlaying = false;
        }
    }

    // --- ★追加機能: 指定フレームへジャンプ ---
    public void JumpToFrame(int targetFrame)
    {
        if (_reader == null || targetFrame < 0) return;

        // 検索のため一時停止扱いにする（シーク直後に勝手に進むのを防ぐため）
        // isPaused = true; // 任意: ジャンプしたら自動でポーズにするならコメントアウトを外す

        try
        {
            // ストリームを先頭にリセット
            _fs.Seek(0, SeekOrigin.Begin);
            _currentFrameCount = 0;

            // ターゲットの直前まで「サイズだけ読んでスキップ」を繰り返す
            // (可変長データのため、計算でジャンプできず、順に辿る必要がある)
            while (_currentFrameCount < targetFrame)
            {
                if (_fs.Position >= _fs.Length) break;

                // 1. dataLen (4 bytes) を読む
                byte[] lenBytes = new byte[4];
                int read = _fs.Read(lenBytes, 0, 4);
                if (read < 4) break;
                
                int dataLen = BitConverter.ToInt32(lenBytes, 0);

                // 2. 残りのヘッダー(numPoints:4 + timestamp:8 = 12 bytes) + 本体(dataLen) をスキップ
                long skipAmount = 12 + dataLen;
                _fs.Seek(skipAmount, SeekOrigin.Current);

                _currentFrameCount++;
            }

            // 目的のフレームに到達したら、普通に1フレーム読み込んで描画更新
            // (ReadNextFrame内で readerを使うため、バッファ整合性のために再度インスタンス化はせずそのまま読む)
            ReadNextFrame();
            
            // ReadNextFrameでカウントが+1されるので、表示を合わせるために戻す
            // (実際の表示上は「再生が終わったフレーム」ではなく「現在表示中のフレーム番号」にしたい場合)
             _currentFrameCount--; 
        }
        catch (Exception e)
        {
            Debug.LogError($"[JumpToFrame] Error: {e.Message}");
        }
    }

    private PointInfo[] ParseFrameData(byte[] data, int numPoints)
    {
        PointInfo[] pointInfos = new PointInfo[numPoints];

        for (int i = 0; i < numPoints; i++)
        {
            int offset = i * BYTES_PER_POINT;

            // X(ROS) -> X(Unity)
            pointInfos[i].position.x = BitConverter.ToSingle(data, offset + 0);
            // Y(ROS) -> Z(Unity)
            pointInfos[i].position.z = BitConverter.ToSingle(data, offset + 4);
            // Z(ROS) -> Y(Unity)
            pointInfos[i].position.y = BitConverter.ToSingle(data, offset + 8);

            pointInfos[i].intensity = BitConverter.ToSingle(data, offset + 12);
            
            if (offset + 16 < data.Length) pointInfos[i].tag = (char)data[offset + 16];
            if (offset + 17 < data.Length) pointInfos[i].line = (char)data[offset + 17];
        }
        return pointInfos;
    }

    public void SaveCurrentFrameAsBin()
    {
        if (_currentPoints == null || _currentPoints.Length == 0)
        {
            Debug.LogWarning("No frame data to save!");
            return;
        }

        string saveFileName = $"frame_{_currentFrameCount}_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
        string savePath = Path.Combine(Directory.GetParent(Application.dataPath).FullName + "\\Assets\\StreamingAssets\\", saveFileName);

        try
        {
            using (FileStream fs = new FileStream(savePath, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                foreach (var p in _currentPoints)
                {
                    // ★正しい座標変換 (Unity -> ROS)
                    writer.Write(p.position.z);  // ROS X (Forward)
                    writer.Write(-p.position.x); // ROS Y (Left)
                    writer.Write(p.position.y);  // ROS Z (Up)
                    writer.Write(p.intensity);
                }
            }
            Debug.Log($"<b>[Saved]</b> Frame saved to: {savePath} ({_currentPoints.Length} points)");
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save bin file: {e.Message}");
        }
    }

    void OnDestroy()
    {
        if (_reader != null) _reader.Close();
        if (_fs != null) _fs.Close();
    }
}

// --- Inspector拡張 (フレーム操作UI) ---
#if UNITY_EDITOR
[CustomEditor(typeof(SingleFilePointCloudPlayer))]
public class SingleFilePointCloudPlayerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        SingleFilePointCloudPlayer player = (SingleFilePointCloudPlayer)target;

        GUILayout.Space(15);
        GUILayout.Label("Playback Controls", EditorStyles.boldLabel);

        // --- フレーム操作エリア ---
        EditorGUI.BeginChangeCheck();
        
        // 現在のフレーム番号を表示・編集可能にする
        int newFrame = EditorGUILayout.IntField("Current Frame", player.CurrentFrameCount);
        
        if (EditorGUI.EndChangeCheck())
        {
            // 数値が書き換わったらそのフレームへジャンプ
            // ジャンプ時はわかりやすくするために一時停止させることが多いが、
            // ここではプレイヤーの状態を尊重（強制ポーズはしない）
            player.JumpToFrame(newFrame);
            
            // もし書き換え時に自動でポーズしたい場合は以下を有効化
            player.isPaused = true; 
        }

        GUILayout.Space(10);
        
        // 保存ボタン
        GUI.backgroundColor = Color.green;
        if (GUILayout.Button("Save Current Frame (.bin)", GUILayout.Height(30)))
        {
            if (player.isPaused)
            {
                player.SaveCurrentFrameAsBin();
            }
            else
            {
                Debug.LogWarning("Please PAUSE the player before saving.");
            }
        }
        GUI.backgroundColor = Color.white;
        
        GUILayout.Space(5);
        GUILayout.Label("Note: Saves to Project Root folder.", EditorStyles.miniLabel);
    }
}
#endif