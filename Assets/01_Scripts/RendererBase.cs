using UnityEngine;

public abstract class RendererBase : MonoBehaviour
{
    // ----------------------------------------------------------
    // メンバ変数.
    protected bool _dataDirty = false;

    public void SetDataDirty() => _dataDirty = true;
}

/* ============================== <EOF> ============================== */

