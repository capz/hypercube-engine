#include "dnd_draw.h"
#include "dnd_storage.h"
#include "dnd_aram.h"
#include <assert.h>
#include <stdio.h>
extern intptr_t dnd_aot_entry(DndManagedHeap *heap);
int texture_host_waits,texture_host_draws,texture_host_publishes;
void dnd_console_write_line(const char *text){puts(text);}
void dnd_console_write_managed_line(DndString *text){(void)text;}
void dnd_draw_backend_options(const float *p,int scene){(void)p;(void)scene;}
int main(void)
{
    static unsigned char storage[4*1024*1024];DndManagedHeap heap;
    dnd_managed_heap_init(&heap,storage,sizeof(storage));dnd_gc_set_stress(true);
    intptr_t result=dnd_aot_entry(&heap);
    if(result||dnd_exception_pending()){
        fprintf(stderr,"Texture LOD AOT result=%ld: %s\n",(long)result,dnd_exception_message());
        DndException *e=(DndException*)dnd_exception_object();if(e&&e->message){for(uint32_t i=0;i<e->message->length;i++)fputc(e->message->chars[i],stderr);fputc('\n',stderr);}return 1;
    }
    assert(dnd_draw_total_bytes()==0&&dnd_draw_live()==0&&dnd_fs_open_handles()==0&&dnd_aram_stat(1)==0);
#ifdef HYPERCUBE_MODEL_RUNTIME
    assert(texture_host_draws>0&&texture_host_publishes==2);
#elif defined(HYPERCUBE_TAIL_RUNTIME)
    assert(texture_host_draws>0&&texture_host_waits==3);
#elif defined(HYPERCUBE_WORLD_RUNTIME)
    assert(texture_host_draws>5&&texture_host_publishes>=2);
#else
    assert(texture_host_draws>100&&texture_host_publishes>=8);
    /* Only two staging buffers, three scene resources, and the AOT device
       shutdown may wait.
       All texture replacement and fence retirement paths must avoid waits. */
    assert(texture_host_waits==6);
#endif
    printf("Texture LOD AOT/GC: coarse/fine, sparse/pair, CRC/short read, cancellation drain, leases, pressure, ARAM, zero-byte trim; draws=%d publications=%d cleanup-waits=%d\n",texture_host_draws,texture_host_publishes,texture_host_waits);
    return 0;
}
