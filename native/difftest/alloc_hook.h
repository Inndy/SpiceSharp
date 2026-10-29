/* Force-included (-include) into every source of faultinject.exe: routes the codec sources' heap calls through
   counting wrappers that can fail on demand. Object-like macros, so the vendored `usr->malloc(...)` callback
   fields are renamed consistently instead of breaking. Not upstream code (MIT). */
#ifndef DT_ALLOC_HOOK_H
#define DT_ALLOC_HOOK_H
#include <stdlib.h>
#include <string.h>
#include <malloc.h>
void *dt_malloc(size_t n);
void *dt_calloc(size_t n, size_t size);
void *dt_realloc(void *p, size_t n);
void dt_free(void *p);
#define malloc dt_malloc
#define calloc dt_calloc
#define realloc dt_realloc
#define free dt_free
#endif
