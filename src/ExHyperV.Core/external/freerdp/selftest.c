#include "exhv_freerdp.h"
#include <stdio.h>
int main(void) { uint32_t result=exhv_selftest();printf("FreeRDP native offline checks: %u (no VM connection)\n",result);return (int)result; }
