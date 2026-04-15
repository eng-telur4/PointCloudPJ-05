using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public class PointCloudFilePlayer : MonoBehaviour
{
    // ----------------------------------------------------------
    // 設定項目.
    [Header("Playback Settings")]
    public float frameRate = 10.0f; // 再生速度 (Hz)
    public bool loop = true;        // ループ再生するか
    public string folderName = "frames"; // StreamingAssets以下のフォルダ名

    [Header("Dependencies")]
    public RendererBase renderer;   // GPUPointCloudRendererをアタッチ

    // ----------------------------------------------------------
    // 定数 (UDP受信側とデータ構造を一致させる)
    private const int BYTES_PER_POINT = 18; 
    private const int FLOAT_SIZE = 4;

    // ----------------------------------------------------------
    // メンバ変数.
    private string[] _filePaths;
    private int _currentFrame = 0;
    private float _timer = 0;
    private bool _isPlaying = false;

    void Start()
    {
        if (renderer == null)
        {
            Debug.LogError("[FilePlayer] Renderer is not assigned.");
            enabled = false;
            return;
        }

        // ファイルパスの取得 (StreamingAssets/frames)
        string dirPath = Path.Combine(Application.streamingAssetsPath, folderName);
        
        if (Directory.Exists(dirPath))
        {
            // .binファイルをすべて取得して名前順にソート
            _filePaths = Directory.GetFiles(dirPath, "*.bin");
            Array.Sort(_filePaths);
            
            if (_filePaths.Length > 0)
            {
                _isPlaying = true;
                Debug.Log($"[FilePlayer] Found {_filePaths.Length} frames. Start playing.");
            }
            else
            {
                Debug.LogWarning($"[FilePlayer] No .bin files found in {dirPath}");
            }
        }
        else
        {
            Debug.LogError($"[FilePlayer] Directory not found: {dirPath}");
        }
    }

    void Update()
    {
        if (!_isPlaying || _filePaths.Length == 0) return;

        // タイマー更新
        _timer += Time.deltaTime;
        if (_timer >= 1.0f / frameRate)
        {
            _timer = 0; // タイマーリセット
            
            // フレーム読み込み処理
            LoadAndSetFrame(_currentFrame);

            // インデックスを進める
            _currentFrame++;

            // ループ処理
            if (_currentFrame >= _filePaths.Length)
            {
                if (loop)
                {
                    _currentFrame = 0;
                }
                else
                {
                    _isPlaying = false;
                    Debug.Log("[FilePlayer] Playback finished.");
                }
            }
        }
    }

    // ファイルを読み込み、コンテナにセットして描画更新を通知する
    private void LoadAndSetFrame(int index)
    {
        try
        {
            string path = _filePaths[index];
            byte[] data = File.ReadAllBytes(path);

            // バイト配列をPointInfo配列に変換
            PointInfo[] pointInfos = ConvertToPointInfoArray(data);

            // コンテナのデータを更新
            PointInfoContainer.ReplacePositions(pointInfos);

            // レンダラーに更新を通知 (ここが重要)
            renderer.SetDataDirty();
        }
        catch (Exception e)
        {
            Debug.LogError($"[FilePlayer] Error loading frame {index}: {e.Message}");
        }
    }

    // バイト配列から構造体配列への変換
    // ※ UdpPointCloudReceiver.ConvertToVector3Array と同じロジック
    private PointInfo[] ConvertToPointInfoArray(byte[] data)
    {
        // サイズチェック
        if (data.Length % BYTES_PER_POINT != 0)
        {
            Debug.LogWarning($"[FilePlayer] Data size {data.Length} is not a multiple of {BYTES_PER_POINT}.");
            return new PointInfo[0];
        }

        int numPoints = data.Length / BYTES_PER_POINT;
        PointInfo[] pointInfos = new PointInfo[numPoints];

        for (int i = 0; i < numPoints; i++)
        {
            int byteIndex = i * BYTES_PER_POINT;

            // UdpPointCloudReceiver と同じ座標変換ロジックを適用
            // X (ROS X) -> Unity X
            pointInfos[i].position.x = BitConverter.ToSingle(data, byteIndex + 0);

            // Y (ROS Y) -> Unity Z (Offset 4)
            pointInfos[i].position.z = BitConverter.ToSingle(data, byteIndex + FLOAT_SIZE);

            // Z (ROS Z) -> Unity Y (Offset 8)
            pointInfos[i].position.y = BitConverter.ToSingle(data, byteIndex + (2 * FLOAT_SIZE));

            // Intensity (Offset 12)
            pointInfos[i].intensity = BitConverter.ToSingle(data, byteIndex + (3 * FLOAT_SIZE));

            // Tag (Offset 16)
            pointInfos[i].tag = (char)data[byteIndex + (4 * FLOAT_SIZE)]; // charは1byteと仮定(C++実装依存だが通常uint8)

            // Line (Offset 17)
            pointInfos[i].line = (char)data[byteIndex + (4 * FLOAT_SIZE) + 1];
        }

        return pointInfos;
    }
}