namespace Hypercube;

// Right-handed look-at camera. Plane tests use projected extents and retain
// boundary-touching boxes; this class has no world/grid/cylinder assumptions.
public sealed class CameraFrustum
{
    private float ex,ey,ez,fx,fy,fz,rx,ry,rz,ux,uy,uz,tx,ty,near,far,sideLength,topLength;
    private readonly float[] planes=new float[12];
    public CameraFrustum(float eyeX,float eyeY,float eyeZ,float targetX,float targetY,float targetZ,
        float upX,float upY,float upZ,float verticalFovDegrees,float aspect,float near,float far)
    {Set(eyeX,eyeY,eyeZ,targetX,targetY,targetZ,upX,upY,upZ,verticalFovDegrees,aspect,near,far);}
    public void Set(float eyeX,float eyeY,float eyeZ,float targetX,float targetY,float targetZ,
        float upX,float upY,float upZ,float verticalFovDegrees,float aspect,float near,float far)
    {
        if(!Finite(eyeX)||!Finite(eyeY)||!Finite(eyeZ)||!Finite(targetX)||!Finite(targetY)||!Finite(targetZ)||!Finite(upX)||!Finite(upY)||!Finite(upZ)||!Finite(verticalFovDegrees)||!Finite(aspect)||!Finite(near)||!Finite(far))throw new ArgumentException("Non-finite camera");
        if(verticalFovDegrees<=0||verticalFovDegrees>=179||aspect<=0||near<=0||far<=near)throw new ArgumentException("Invalid camera projection");
        float x=targetX-eyeX,y=targetY-eyeY,z=targetZ-eyeZ,n=(float)Math.Sqrt(x*x+y*y+z*z);
        if(!(n>0)||!Finite(n))throw new ArgumentException("Invalid camera direction");float nfx=x/n,nfy=y/n,nfz=z/n;
        x=nfy*upZ-nfz*upY;y=nfz*upX-nfx*upZ;z=nfx*upY-nfy*upX;n=(float)Math.Sqrt(x*x+y*y+z*z);
        if(!(n>0)||!Finite(n))throw new ArgumentException("Parallel camera up");float nrx=x/n,nry=y/n,nrz=z/n;
        float nux=nry*nfz-nrz*nfy,nuy=nrz*nfx-nrx*nfz,nuz=nrx*nfy-nry*nfx;
        float nty=(float)Math.Tan(verticalFovDegrees*Math.PI/360),ntx=nty*aspect;
        if(!Finite(ntx)||!Finite(nty))throw new ArgumentException("Invalid camera projection");
        float side=(float)Math.Sqrt(1+ntx*ntx),top=(float)Math.Sqrt(1+nty*nty);
        if(!Finite(side)||!Finite(top))throw new ArgumentException("Projection exceeds finite range");
        ex=eyeX;ey=eyeY;ez=eyeZ;this.near=near;this.far=far;
        fx=nfx;fy=nfy;fz=nfz;rx=nrx;ry=nry;rz=nrz;ux=nux;uy=nuy;uz=nuz;tx=ntx;ty=nty;sideLength=side;topLength=top;
        planes[0]=fx*tx+rx;planes[1]=fy*tx+ry;planes[2]=fz*tx+rz;planes[3]=fx*tx-rx;planes[4]=fy*tx-ry;planes[5]=fz*tx-rz;
        planes[6]=fx*ty+ux;planes[7]=fy*ty+uy;planes[8]=fz*ty+uz;planes[9]=fx*ty-ux;planes[10]=fy*ty-uy;planes[11]=fz*ty-uz;

    }
    private static bool Finite(float x)=>!float.IsNaN(x)&&!float.IsInfinity(x);
    private static bool Plane(float x,float y,float z,float hx,float hy,float hz,float nx,float ny,float nz)
        =>nx*x+ny*y+nz*z>=-(Math.Abs(nx)*hx+Math.Abs(ny)*hy+Math.Abs(nz)*hz);
    public bool Box(float x0,float y0,float z0,float x1,float y1,float z1)
    {
        if(!Finite(x0)||!Finite(y0)||!Finite(z0)||!Finite(x1)||!Finite(y1)||!Finite(z1)||!(x1>=x0&&y1>=y0&&z1>=z0))throw new ArgumentException("Invalid bounds");
        float x=(x0+x1)*.5f-ex,y=(y0+y1)*.5f-ey,z=(z0+z1)*.5f-ez,hx=(x1-x0)*.5f,hy=(y1-y0)*.5f,hz=(z1-z0)*.5f;
        float d=fx*x+fy*y+fz*z,r=Math.Abs(fx)*hx+Math.Abs(fy)*hy+Math.Abs(fz)*hz;
        if(d+r<near||d-r>far)return false;
        for(int i=0;i<12;i+=3)if(!Plane(x,y,z,hx,hy,hz,planes[i],planes[i+1],planes[i+2]))return false;
        return true;
    }
    public bool Sphere(float x,float y,float z,float radius)
    {
        if(!Finite(x)||!Finite(y)||!Finite(z)||!(radius>=0)||float.IsInfinity(radius))throw new ArgumentOutOfRangeException();
        x-=ex;y-=ey;z-=ez;float d=fx*x+fy*y+fz*z;
        return d+radius>=near&&d-radius<=far&&Math.Abs(rx*x+ry*y+rz*z)<=d*tx+radius*sideLength&&Math.Abs(ux*x+uy*y+uz*z)<=d*ty+radius*topLength;
    }
}
