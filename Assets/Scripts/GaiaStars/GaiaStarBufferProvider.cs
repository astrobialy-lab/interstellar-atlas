using System.Collections.Generic;
using UnityEngine;

public abstract class GaiaStarBufferProvider : MonoBehaviour
{
    public abstract void GetLoadedStarBuffers(
        List<GraphicsBuffer> buffers,
        List<int> counts,
        List<float> fades
    );
}