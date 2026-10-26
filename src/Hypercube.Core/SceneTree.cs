namespace Hypercube;

public sealed class SceneTree
{
    public readonly int[] X = new int[85], Z = new int[85], Span = new int[85], Child = new int[85];
    private int count;
    public SceneTree(){Build(0,0,8);}
    private int Build(int x,int z,int span)
    {
        int node=count++;X[node]=x;Z[node]=z;Span[node]=span;Child[node]=-1;
        if(span>1)
        {
            Child[node]=count;
            Build(x,z,span/2);Build(x+span/2,z,span/2);
            Build(x,z+span/2,span/2);Build(x+span/2,z+span/2,span/2);
        }
        return node;
    }
    public int SubtreeSize(int node)
    {
        // A complete quadtree has (4*leafCount-1)/3 nodes.
        return (4*Span[node]*Span[node]-1)/3;
    }
    public int ChildAt(int node,int quadrant)=>Child[node]+quadrant*SubtreeSize(Child[node]);
    public bool HasCells(int node)=>X[node]<6&&Z[node]<4;
    public int Leaf(int cell)
    {
        int x=cell%6,z=cell/6,node=0;
        while(Span[node]>1)
        {
            int half=Span[node]/2;
            node=ChildAt(node,(z>=Z[node]+half?2:0)+(x>=X[node]+half?1:0));
        }
        return node;
    }
}
