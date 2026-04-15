using UnityEngine;

public static class PointInfoContainer
{
    // ----------------------------------------------------------
    // 定数宣言.
    private static int MAX_CAPACITY = 100000;
    
    // ----------------------------------------------------------
    // ゲッタセッタ.
    public static PointInfo[] AllPointInfos { get; private set; } 
    public static int PointCount => AllPointInfos != null ? AllPointInfos.Length : 0;
    public static int MaxCapacity => MAX_CAPACITY;


    public static void ReplacePositions(PointInfo[] newPositions)
    {
        int count = Mathf.Min(newPositions.Length, MAX_CAPACITY);
        
        AllPointInfos = new PointInfo[count];
        System.Array.Copy(newPositions, AllPointInfos, count);
    }
}

/* ============================== <EOF> ============================== */
