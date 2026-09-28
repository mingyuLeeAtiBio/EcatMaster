#ifndef _WIN32
#  define _GNU_SOURCE
#  include <pthread.h>
#  include <sched.h>
#  include <time.h>
#else
#  ifndef _CRT_SECURE_NO_WARNINGS
#    define _CRT_SECURE_NO_WARNINGS
#  endif
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN   /* windows.h 가 winsock.h 를 끌어와 SOEM 의 winsock2.h 와 충돌하는 것을 방지 */
#  endif
#endif

#include <stdio.h>
#include <string.h>

/* SOEM 헤더가 먼저 (Windows 에서는 내부적으로 winsock2.h 포함) */
#include "ethercat.h"
#include "soemshim.h"

#ifdef _WIN32
#  include <windows.h>
#  include <timeapi.h>
#  ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#    define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#  endif
#endif

/* simple_test.c 에서 쓰는 감시용 타임아웃(us). SOEM 헤더에는 없다. */
#ifndef EC_TIMEOUTMON
#define EC_TIMEOUTMON 500
#endif

static char          g_err[256];
static shim_po2so_cb g_cb = NULL;
static int           g_inited = 0;

static int po2so_trampoline(uint16 slave)
{
    return g_cb ? g_cb(slave) : 1;
}

/* ------------------------------------------------------------------ adapters */

#define MAX_ADAPTERS 32
static char g_adapter_name[MAX_ADAPTERS][256];
static char g_adapter_desc[MAX_ADAPTERS][256];
static int  g_adapter_count = 0;

int shim_adapter_count(void)
{
    ec_adaptert *list = ec_find_adapters();
    g_adapter_count = 0;
    for (ec_adaptert *a = list; a && g_adapter_count < MAX_ADAPTERS; a = a->next)
    {
        snprintf(g_adapter_name[g_adapter_count], 256, "%s", a->name);
        snprintf(g_adapter_desc[g_adapter_count], 256, "%s", a->desc);
        g_adapter_count++;
    }
    ec_free_adapters(list);
    return g_adapter_count;
}

const char *shim_adapter_name(int i) { return (i >= 0 && i < g_adapter_count) ? g_adapter_name[i] : ""; }
const char *shim_adapter_desc(int i) { return (i >= 0 && i < g_adapter_count) ? g_adapter_desc[i] : ""; }

/* ------------------------------------------------------------------ init */

int shim_init(const char *ifname)
{
    if (ec_init(ifname))
    {
        g_inited = 1;
        g_err[0] = 0;
        return 1;
    }
#ifdef _WIN32
    snprintf(g_err, sizeof g_err, "ec_init(%s) failed: Npcap(WinPcap compatible mode) installed? adapter name must be \\Device\\NPF_{GUID}", ifname);
#else
    snprintf(g_err, sizeof g_err, "ec_init(%s) failed: no raw socket access (need CAP_NET_RAW or root)", ifname);
#endif
    return 0;
}

void shim_close(void)
{
    if (g_inited)
    {
        ec_close();
        g_inited = 0;
    }
}

int shim_config_init(void)
{
    int n = ec_config_init(FALSE);
    if (n <= 0)
        snprintf(g_err, sizeof g_err, "ec_config_init: no slaves found");
    return n;
}

void shim_set_po2so_callback(shim_po2so_cb cb)
{
    g_cb = cb;
    for (int i = 1; i <= ec_slavecount; i++)
        ec_slave[i].PO2SOconfig = cb ? po2so_trampoline : NULL;
}

int shim_config_map(void *iomap, int iomap_size)
{
    (void)iomap_size; /* SOEM 1.4 은 크기를 받지 않는다. C# 쪽에서 반환값으로 검사 */
    return ec_config_map(iomap);
}

int shim_dc_config(void)
{
    return ec_configdc();
}

int64_t shim_dc_time(void)
{
    return ec_DCtime;
}

int shim_slave_hasdc(int slave)
{
    return ec_slave[slave].hasdc ? 1 : 0;
}

void shim_dcsync0(int slave, int active, uint32_t cycle_ns, int32_t shift_ns)
{
    ec_dcsync0((uint16)slave, active ? TRUE : FALSE, cycle_ns, shift_ns);
}

/* ------------------------------------------------------------------ slaves */

int          shim_slave_count(void)            { return ec_slavecount; }
const char  *shim_slave_name(int s)            { return ec_slave[s].name; }
uint32_t     shim_slave_vendor_id(int s)       { return ec_slave[s].eep_man; }
uint32_t     shim_slave_product_code(int s)    { return ec_slave[s].eep_id; }
uint16_t     shim_slave_state(int s)           { return ec_slave[s].state; }
uint16_t     shim_slave_al_status(int s)       { return ec_slave[s].ALstatuscode; }
const char  *shim_al_status_text(uint16_t c)   { return ec_ALstatuscode2string(c); }
uint8_t     *shim_slave_outputs(int s)         { return ec_slave[s].outputs; }
int          shim_slave_output_bytes(int s)    { return (int)ec_slave[s].Obytes; }
uint8_t     *shim_slave_inputs(int s)          { return ec_slave[s].inputs; }
int          shim_slave_input_bytes(int s)     { return (int)ec_slave[s].Ibytes; }
int          shim_slave_output_bits(int s)     { return (int)ec_slave[s].Obits; }
int          shim_slave_input_bits(int s)      { return (int)ec_slave[s].Ibits; }
int          shim_slave_output_startbit(int s) { return (int)ec_slave[s].Ostartbit; }
int          shim_slave_input_startbit(int s)  { return (int)ec_slave[s].Istartbit; }

/* ------------------------------------------------------------------ state */

int shim_request_state(int slave, uint16_t state)
{
    ec_slave[slave].state = state;
    return ec_writestate(slave);
}

uint16_t shim_state_check(int slave, uint16_t state, int timeout_us)
{
    return ec_statecheck(slave, state, timeout_us);
}

void shim_read_state(void)
{
    ec_readstate();
}

/* ------------------------------------------------------------------ process data */

int shim_send_processdata(void)
{
    return ec_send_processdata();
}

int shim_receive_processdata(int timeout_us)
{
    return ec_receive_processdata(timeout_us);
}

int shim_expected_wkc(void)
{
    return (ec_group[0].outputsWKC * 2) + ec_group[0].inputsWKC;
}

/* ------------------------------------------------------------------ SDO */

int shim_sdo_write(int slave, uint16_t index, uint8_t subindex, int complete_access,
                   int size, const void *data, int timeout_us)
{
    return ec_SDOwrite(slave, index, subindex, complete_access ? TRUE : FALSE, size, (void *)data, timeout_us);
}

int shim_sdo_read(int slave, uint16_t index, uint8_t subindex, int complete_access,
                  int *size, void *data, int timeout_us)
{
    return ec_SDOread(slave, index, subindex, complete_access ? TRUE : FALSE, size, data, timeout_us);
}

/* ------------------------------------------------------------------ supervision */

int shim_check_and_recover(int wkc, int expected_wkc)
{
    int not_op = 0;

    if (wkc < expected_wkc || ec_group[0].docheckstate)
    {
        ec_group[0].docheckstate = FALSE;
        ec_readstate();

        for (int slave = 1; slave <= ec_slavecount; slave++)
        {
            if (ec_slave[slave].group != 0)
                continue;

            if (ec_slave[slave].state != EC_STATE_OPERATIONAL)
            {
                ec_group[0].docheckstate = TRUE;
                not_op++;

                if (ec_slave[slave].state == (EC_STATE_SAFE_OP + EC_STATE_ERROR))
                {
                    ec_slave[slave].state = (EC_STATE_SAFE_OP + EC_STATE_ACK);
                    ec_writestate(slave);
                }
                else if (ec_slave[slave].state == EC_STATE_SAFE_OP)
                {
                    ec_slave[slave].state = EC_STATE_OPERATIONAL;
                    ec_writestate(slave);
                }
                else if (ec_slave[slave].state > EC_STATE_NONE)
                {
                    if (ec_reconfig_slave(slave, EC_TIMEOUTMON))
                        ec_slave[slave].islost = FALSE;
                }
                else if (!ec_slave[slave].islost)
                {
                    ec_statecheck(slave, EC_STATE_OPERATIONAL, EC_TIMEOUTRET);
                    if (ec_slave[slave].state == EC_STATE_NONE)
                        ec_slave[slave].islost = TRUE;
                }
            }

            if (ec_slave[slave].islost)
            {
                if (ec_slave[slave].state == EC_STATE_NONE)
                {
                    if (ec_recover_slave(slave, EC_TIMEOUTMON))
                        ec_slave[slave].islost = FALSE;
                }
                else
                {
                    ec_slave[slave].islost = FALSE;
                }
            }
        }
    }
    return not_op;
}

/* ------------------------------------------------------------------ realtime helpers */

#ifndef _WIN32

int shim_set_realtime(int priority)
{
    struct sched_param p;
    memset(&p, 0, sizeof p);
    p.sched_priority = priority;
    return pthread_setschedparam(pthread_self(), SCHED_FIFO, &p) == 0 ? 1 : 0;
}

int shim_pin_cpu(int cpu)
{
    cpu_set_t set;
    CPU_ZERO(&set);
    CPU_SET(cpu, &set);
    return pthread_setaffinity_np(pthread_self(), sizeof set, &set) == 0 ? 1 : 0;
}

int64_t shim_now_ns(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000000LL + ts.tv_nsec;
}

void shim_sleep_until_ns(int64_t t)
{
    struct timespec ts;
    ts.tv_sec = (time_t)(t / 1000000000LL);
    ts.tv_nsec = (long)(t % 1000000000LL);
    while (clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME, &ts, NULL) != 0)
    {
        /* EINTR 이면 다시 */
    }
}

#else /* _WIN32 ------------------------------------------------------------- */

static LARGE_INTEGER g_qpf;
static HANDLE        g_timer = NULL;

static void win_init_clock(void)
{
    if (g_qpf.QuadPart == 0)
        QueryPerformanceFrequency(&g_qpf);
}

int shim_set_realtime(int priority)
{
    (void)priority;
    timeBeginPeriod(1);
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    return SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL) ? 1 : 0;
}

int shim_pin_cpu(int cpu)
{
    return SetThreadAffinityMask(GetCurrentThread(), (DWORD_PTR)1 << cpu) != 0 ? 1 : 0;
}

int64_t shim_now_ns(void)
{
    LARGE_INTEGER c;
    win_init_clock();
    QueryPerformanceCounter(&c);
    /* 오버플로 방지: 초/나머지 분리 */
    int64_t sec = c.QuadPart / g_qpf.QuadPart;
    int64_t rem = c.QuadPart % g_qpf.QuadPart;
    return sec * 1000000000LL + rem * 1000000000LL / g_qpf.QuadPart;
}

/*
 * Windows 는 실시간 OS 가 아니므로: 고해상도 대기 타이머로 남은 시간 - 0.3ms 까지 자고,
 * 마지막 0.3ms 는 스핀한다. 1~2ms 사이클에서 대개 수십 us 지터.
 */
void shim_sleep_until_ns(int64_t t)
{
    if (!g_timer)
    {
        g_timer = CreateWaitableTimerExW(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!g_timer)
            g_timer = CreateWaitableTimerW(NULL, TRUE, NULL);
    }

    for (;;)
    {
        int64_t rem = t - shim_now_ns();
        if (rem <= 0)
            return;

        if (rem > 300000 && g_timer)
        {
            LARGE_INTEGER due;
            due.QuadPart = -((rem - 300000) / 100); /* 100ns 단위, 음수 = 상대 시간 */
            if (SetWaitableTimer(g_timer, &due, 0, NULL, NULL, FALSE))
                WaitForSingleObject(g_timer, INFINITE);
            else
                Sleep(0);
        }
        else
        {
            YieldProcessor();
        }
    }
}

#endif

const char *shim_last_error(void)
{
    if (ec_iserror())
        return ec_elist2string();
    return g_err;
}
