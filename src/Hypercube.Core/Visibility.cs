namespace Hypercube;

// Camera-aligned conservative tests for the validation camera. The engine API
// keeps these separate from cell readiness and streaming priority.
public static class Visibility
{
    private const float Tan=.52056705f,TanX=Tan*4/3;
    private static float Abs(float x)=>x<0?-x:x;
    private static readonly CameraFrustum validationCamera=new CameraFrustum(0,12,16,0,0,0,0,1,0,55,4f/3,.1f,80);
    public static bool Frustum(float x0,float y0,float z0,float x1,float y1,float z1,float focusX,float focusZ)
        =>validationCamera.Box(x0-focusX,y0,z0-focusZ,x1-focusX,y1,z1-focusZ);
    public static bool Occluded(float x0,float y0,float z0,float x1,float y1,float z1,float focusX,float focusZ,
                                float ox0,float oy0,float oz,float ox1,float oy1)
    {
        // Only a verified, camera-facing opaque rectangular front face is used.
        // Its inscribed projected rectangle cannot occlude pixels outside it.
        float bottomDepth=.6f*(12-oy0)+.8f*(focusZ+16-oz);
        float topDepth=.6f*(12-oy1)+.8f*(focusZ+16-oz);
        if(bottomDepth<=0||topDepth<=0)return false;
        float left0=(ox0-focusX)/(bottomDepth*TanX),left1=(ox0-focusX)/(topDepth*TanX);
        float right0=(ox1-focusX)/(bottomDepth*TanX),right1=(ox1-focusX)/(topDepth*TanX);
        float left=left0>left1?left0:left1,right=right0<right1?right0:right1;
        float bottom=(.8f*(oy0-12)-.6f*(oz-focusZ-16))/(bottomDepth*Tan);
        float top=(.8f*(oy1-12)-.6f*(oz-focusZ-16))/(topDepth*Tan);
        float far=bottomDepth>topDepth?bottomDepth:topDepth;
        for(int k=0;k<8;k++)
        {
            float x=(k&1)==0?x0:x1,y=(k&2)==0?y0:y1,z=(k&4)==0?z0:z1;
            float d=.6f*(12-y)+.8f*(focusZ+16-z);
            if(d<=far+.01f)return false;
            float px=(x-focusX)/(d*TanX),py=(.8f*(y-12)-.6f*(z-focusZ-16))/(d*Tan);
            if(px<left||px>right||py<bottom||py>top)return false;
        }
        return true;
    }
}
