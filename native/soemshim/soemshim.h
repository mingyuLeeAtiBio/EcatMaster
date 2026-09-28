/*
 * soemshim - SOEM 1.4.x 위의 얇은 C ABI 래퍼.
 * C# (EcatMaster.Core/Native/SoemNative.cs) 가 P/Invoke 로 호출한다.
 * ec_slave[] 같은 큰 구조체를 C# 에서 직접 마샬링하지 않기 위해 필요한 필드만 함수로 노출한다.
 * Linux(raw socket) 와 Windows(Npcap, WinPcap 호환 모드) 모두 지원.
 */
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#  define SHIM_API __declspec(dllexport)
#else
#  define SHIM_API __attribute__((visibility("default")))
#endif

/* PreOp->SafeOp 훅. 1 = 성공, 0 = 실패 */
typedef int (*shim_po2so_cb)(uint16_t slave);

/* ---- NIC 목록 (Windows 는 \Device\NPF_{GUID} 이름을 ec_init 에 넘겨야 한다) ---- */
SHIM_API int          shim_adapter_count(void);                   /* 목록 갱신 후 개수 */
SHIM_API const char  *shim_adapter_name(int i);                   /* ec_init 용 이름 */
SHIM_API const char  *shim_adapter_desc(int i);                   /* 사람이 읽는 설명 */

/* ---- 초기화 / 설정 ---- */
SHIM_API int          shim_init(const char *ifname);              /* 1 ok, 0 fail */
SHIM_API void         shim_close(void);
SHIM_API int          shim_config_init(void);                     /* 슬레이브 수, <=0 실패 */
SHIM_API void         shim_set_po2so_callback(shim_po2so_cb cb);  /* 모든 슬레이브에 등록 */
SHIM_API int          shim_config_map(void *iomap, int iomap_size);/* 사용한 바이트 수 */
SHIM_API int          shim_dc_config(void);                       /* ec_configdc: 1 = DC 슬레이브 있음 */
SHIM_API int64_t      shim_dc_time(void);                         /* 마지막 수신 프레임의 DC 기준 시각 [ns] */
SHIM_API int          shim_slave_hasdc(int slave);
SHIM_API void         shim_dcsync0(int slave, int active, uint32_t cycle_ns, int32_t shift_ns);

/* ---- 슬레이브 정보 (slave 는 1-based) ---- */
SHIM_API int          shim_slave_count(void);
SHIM_API const char  *shim_slave_name(int slave);
SHIM_API uint32_t     shim_slave_vendor_id(int slave);
SHIM_API uint32_t     shim_slave_product_code(int slave);
SHIM_API uint16_t     shim_slave_state(int slave);
SHIM_API uint16_t     shim_slave_al_status(int slave);
SHIM_API const char  *shim_al_status_text(uint16_t code);
SHIM_API uint8_t     *shim_slave_outputs(int slave);
SHIM_API int          shim_slave_output_bytes(int slave);
SHIM_API uint8_t     *shim_slave_inputs(int slave);
SHIM_API int          shim_slave_input_bytes(int slave);
/* 비트 단위 PDO (EL1004 처럼 8비트 미만 터미널용). startbit = 첫 바이트 안의 비트 오프셋 */
SHIM_API int          shim_slave_output_bits(int slave);
SHIM_API int          shim_slave_input_bits(int slave);
SHIM_API int          shim_slave_output_startbit(int slave);
SHIM_API int          shim_slave_input_startbit(int slave);

/* ---- 상태 전이 ---- */
SHIM_API int          shim_request_state(int slave, uint16_t state); /* slave 0 = all */
SHIM_API uint16_t     shim_state_check(int slave, uint16_t state, int timeout_us);
SHIM_API void         shim_read_state(void);

/* ---- 프로세스 데이터 ---- */
SHIM_API int          shim_send_processdata(void);
SHIM_API int          shim_receive_processdata(int timeout_us);   /* WKC */
SHIM_API int          shim_expected_wkc(void);

/* ---- SDO ---- */
SHIM_API int          shim_sdo_write(int slave, uint16_t index, uint8_t subindex, int complete_access,
                                     int size, const void *data, int timeout_us);
SHIM_API int          shim_sdo_read(int slave, uint16_t index, uint8_t subindex, int complete_access,
                                    int *size, void *data, int timeout_us);

/* ---- 감시/복구 (simple_test.c ecatcheck 1회). 반환: OP 가 아닌 슬레이브 수 ---- */
SHIM_API int          shim_check_and_recover(int wkc, int expected_wkc);

/* ---- 실시간 보조 ---- */
SHIM_API int          shim_set_realtime(int priority);  /* Linux: SCHED_FIFO / Windows: TIME_CRITICAL. 1 ok */
SHIM_API int          shim_pin_cpu(int cpu);            /* 1 ok */
SHIM_API int64_t      shim_now_ns(void);                /* 단조 증가 클록 */
SHIM_API void         shim_sleep_until_ns(int64_t t);   /* 절대 시각까지 대기 */

SHIM_API const char  *shim_last_error(void);

#ifdef __cplusplus
}
#endif
