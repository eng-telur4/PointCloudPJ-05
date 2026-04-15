using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Utility
{
    /// <summary>
    /// 汎用タイマー.
    /// </summary>
    public class Timer
    {
        // ----------------------------------------------------------
        #region ゲッタセッタ.

        /// <summary>
        /// 制限時間が来たら true を返す.
        /// </summary>
        public bool IsTime { get; private set; }

        /// <summary>
        /// 動作中は true を返す.
        /// </summary>
        public bool IsPlay { get; private set; }

        /// <summary>
        /// 制限時間.
        /// </summary>
        public float LimitTime { get; private set; }

        /// <summary>
        /// 経過時間.
        /// </summary>
        public float PlayTime { get; private set; }

        /// <summary>
        /// 進行度 (0～1).
        /// </summary>
        public float Progress
        {
            get
            {
                float value = PlayTime / LimitTime;
                return Mathf.Clamp(value, value, 1f);
            }
        }

        #endregion

        // ----------------------------------------------------------
        #region  コンストラクタ.

        /// <summary>
        /// コンストラクタ.
        /// </summary>
        /// <param name="limit_time">制限時間（ミリ秒）.</param>
        public Timer(float limit_time)
        {
            this.LimitTime = limit_time / 1000f;
            this.PlayTime = 0f;

            IsTime = false;
            IsPlay = false;
        }

        #endregion

        // ----------------------------------------------------------
        // Public メソッド.

        /// <summary>
        /// 更新処理.
        /// </summary>
        public void Update()
        {
            if (this.LimitTime <= this.PlayTime)
            {
                this.IsTime = true;
                this.IsPlay = false;
                return;
            }

            this.PlayTime += Time.deltaTime;
            this.IsPlay = true;
        }

        /// <summary>
        /// 初期化してもう一度繰り返す.
        /// </summary>
        public void Reset()
        {
            this.PlayTime = 0f;
            IsTime = false;
        }

        /// <summary>
        /// タイムリミットを再設定してもう一度繰り返す.
        /// </summary>
        /// <param name="limit_time">制限時間（ミリ秒）.</param>
        public void Reset(float limit_time)
        {
            this.PlayTime = 0f;
            this.LimitTime = limit_time / 1000f;
            IsTime = false;
        }
    }
}

/* ============================== <EOF> ============================== */
