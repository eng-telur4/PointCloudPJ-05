using UnityEngine;

public class TestPointCloudRenderer : MonoBehaviour
{
    // ----------------------------------------------------------
    // 設定項目.
    [SerializeField] private int pointCloudSize = 10000;
    [SerializeField] private float time = 1000f;
    [SerializeField] private RendererBase renderer = null;

    // ----------------------------------------------------------
    // メンバ変数.
    private Utility.Timer _timer = null;

    private void Start()
    {
        _timer = new Utility.Timer(time);
        PointInfoContainer.ReplacePositions(CreateTestData());
        renderer.SetDataDirty();
    }


    private void Update()
    {
        _timer.Update();

        if(_timer.IsTime)
        {
            PointInfoContainer.ReplacePositions(CreateTestData());
            renderer.SetDataDirty();
            _timer.Reset();
        }
    }

    private PointInfo[] CreateTestData()
    {
        var points = new PointInfo[pointCloudSize];
        for (int i = 0; i < pointCloudSize; i++)
        {
            points[i] = new PointInfo
            {
                position = new Vector3(
                    Random.Range(-5f, 5f),
                    Random.Range(-5f, 5f),
                    Random.Range(-5f, 5f)
                ),
                intensity = Random.Range(0, 128f),
                tag = 'A',
                line = '1'
            };
        }

        return points;
    }
}

/* ============================== <EOF> ============================== */
