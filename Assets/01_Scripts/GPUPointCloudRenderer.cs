using UnityEngine;
using System;
using System.IO; 
using System.Text; 
using System.Runtime.InteropServices;

public class GPUPointCloudRenderer : RendererBase
{
    // ----------------------------------------------------------
    // 設定項目.
    [Header("Rendering")]
    [SerializeField] private Material material = null;
    [SerializeField] private Mesh quadMesh = null;
    [SerializeField] private Gradient gradient = null;
    [SerializeField, Range(1f, 255f)] private float intensityThreshold = 128f;

    [Header("Size")]
    [SerializeField] private float baseSize = 0.2f;
    [SerializeField] private float sizeScale = 1.0f;

    // ----------------------------------------------------------
    // メンバ変数.
    private PointInfo[] _points;
    private ComputeBuffer _pointBuffer;
    private ComputeBuffer _colorBuffer;

    // 【ログ保存用】
    private string _logFileName = null;
    private const string FILE_PATH = "PointDataLogs";
    // ファイル名用の時刻フォーマット
    private const string TIME_FORMAT_FILE = "yyyyMMdd_HHmmss"; 
    private bool _headerWritten = false;

    // 上限フレーム数 (5000回で停止)
    private const int MAX_LOG_COUNT = 5000; 
    private int _loggedFrameCount = 0; // 実際にログ保存した回数

    private void Start()
    {
        _points = PointInfoContainer.AllPointInfos;
        Debug.Log($"[Logger] Targeting {MAX_LOG_COUNT} frames for data logging.");
    }

    private void Update()
    {
        if (_dataDirty)
        {
            Renderer();
            _dataDirty = false;
        }
    }

    private void OnDestroy()
    {
        ReleaseBuffers();
    }

    private void Renderer()
    {
        _points = PointInfoContainer.AllPointInfos;
        if (_points == null || _points.Length == 0) return;

        // ポジションデータを配列にコピー
        var pb = new Vector4[_points.Length];
        var cb = new Vector4[_points.Length];
        
        for (int i = 0; i < _points.Length; i++)
        {
            pb[i] = new Vector4(_points[i].position.x, _points[i].position.y, _points[i].position.z, 1.0f); 
            float intensity = Mathf.Clamp01(_points[i].intensity / 128f);
            Color c = gradient != null ? gradient.Evaluate(intensity) : Color.white;
            cb[i] = new Vector4(c.r, c.g, c.b, c.a);
        }

        // --- CSV保存処理 (5000回制限) ---
        if (_loggedFrameCount < MAX_LOG_COUNT)
        {
            SaveLog();
            _loggedFrameCount++;

            // ちょうど5000回目を保存し終わったら完了ログを出す
            if (_loggedFrameCount == MAX_LOG_COUNT)
            {
                Debug.Log($"[Logger] COMPLETED: Recorded {MAX_LOG_COUNT} frames. Logging stopped.");
            }
        }

        SetBuffers(pb, cb);
    }

    private void SetBuffers(Vector4[] pointData, Vector4[] colorData)
    {
        ReleaseBuffers();

        int count = pointData.Length;
        _pointBuffer = new ComputeBuffer(count, sizeof(float) * 4);
        _pointBuffer.SetData(pointData);

        _colorBuffer = new ComputeBuffer(count, sizeof(float) * 4);
        _colorBuffer.SetData(colorData);

        material.SetBuffer("_PointBuffer", _pointBuffer);
        material.SetBuffer("_ColorBuffer", _colorBuffer);
        material.SetFloat("_BaseSize", baseSize);
        material.SetFloat("_SizeScale", sizeScale);
    }

    private void ReleaseBuffers()
    {
        if (_pointBuffer != null) { _pointBuffer.Release(); _pointBuffer = null; }
        if (_colorBuffer != null) { _colorBuffer.Release(); _colorBuffer = null; }
    }

    private void OnRenderObject()
    {
        if (material == null || quadMesh == null || _pointBuffer == null || _colorBuffer == null) return;
        if (_points == null || _points.Length <= 0) return;

        material.SetFloat("_BaseSize", baseSize);
        material.SetFloat("_SizeScale", sizeScale);
        material.SetPass(0);
        Graphics.DrawMeshInstancedProcedural(quadMesh, 0, material, new Bounds(Vector3.zero, Vector3.one * 100f), _points.Length);
    }

    // ----------------------------------------------------------
    // ログ保存メソッド
    // 形式: 点群数, UNIX時刻(秒.ナノ秒)
    // ----------------------------------------------------------
    private void SaveLog()
    {
        // 1. ファイル名決定 (初回のみ)
        if (_logFileName == null)
        {
            string timestamp = DateTime.Now.ToString(TIME_FORMAT_FILE);
            _logFileName = $"e2elog_{timestamp}.csv";
            
            // プロジェクトルート/PointDataLogs に保存
            string fullPath = Path.Combine(Application.dataPath + "/..", FILE_PATH);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
            _logFileName = Path.Combine(fullPath, _logFileName);
            
            Debug.Log($"[Logger] Started writing to: {_logFileName}");
        }

        // 2. 現在時刻を UNIXタイムスタンプ (秒単位, 小数点以下含む) で取得
        // Ubuntu側 (C++) の timebase * 1.0e-9 と形式を一致させます
        TimeSpan t = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double unixTimestamp = t.TotalSeconds;
        
        // 3. データの準備
        int pointCount = _points.Length;

        // 4. 書き込み
        try
        {
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
            Debug.LogError($"[Logger] Failed to write log file: {e.Message}");
        }
    }
}