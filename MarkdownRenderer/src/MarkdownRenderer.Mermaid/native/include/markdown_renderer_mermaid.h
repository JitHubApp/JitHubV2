/* Generated from native/abi/mmir-v1.json. Do not edit by hand. */
#ifndef MARKDOWN_RENDERER_MERMAID_H
#define MARKDOWN_RENDERER_MERMAID_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define MMIR_API __declspec(dllexport)
#define MMIR_CALL __cdecl
#else
#define MMIR_API
#define MMIR_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define MMIR_ABI_VERSION ((uint32_t)0x00010000u)

typedef enum mmir_status {
    MMIR_STATUS_SUCCESS = 0,
    MMIR_STATUS_INVALID_INPUT = 1,
    MMIR_STATUS_BUDGET_EXCEEDED = 2,
    MMIR_STATUS_CANCELLED = 3,
    MMIR_STATUS_TIMED_OUT = 4,
    MMIR_STATUS_UNSUPPORTED_LAYOUT = 5,
    MMIR_STATUS_INTERNAL_FAILURE = 6,
    MMIR_STATUS_INVALID_HANDLE = 7,
    MMIR_STATUS_INCOMPATIBLE_ABI = 8,
    MMIR_STATUS_PANIC = 9,
    MMIR_STATUS_BUSY = 10,
    MMIR_STATUS_INVALID_SCENE = 11
} mmir_status;

typedef uintptr_t mmir_engine_handle;
typedef uintptr_t mmir_cancellation_handle;
typedef uintptr_t mmir_buffer_handle;

#pragma pack(push, 8)
typedef struct mmir_engine_options {
    uint32_t struct_size;
    uint32_t abi_version;
    uint64_t max_working_memory_bytes;
    uint32_t max_concurrent_renders;
    uint32_t reserved;
} mmir_engine_options;

typedef struct mmir_render_options {
    uint32_t struct_size;
    uint32_t layout;
    uint32_t theme;
    uint32_t max_source_bytes;
    uint32_t max_nodes;
    uint32_t max_edges;
    uint32_t max_depth;
    uint32_t max_label_bytes;
    uint32_t max_scene_bytes;
    uint32_t deadline_milliseconds;
    uint32_t reserved0;
    uint32_t reserved1;
} mmir_render_options;
#pragma pack(pop)

MMIR_API uint32_t MMIR_CALL mmir_get_abi_version(void);
MMIR_API mmir_status MMIR_CALL mmir_engine_create(
    const mmir_engine_options* options,
    const uint8_t* font_catalog,
    uint32_t font_catalog_length,
    mmir_engine_handle* out_engine);
MMIR_API mmir_status MMIR_CALL mmir_engine_release(mmir_engine_handle engine);
MMIR_API mmir_status MMIR_CALL mmir_cancellation_create(mmir_cancellation_handle* out_cancellation);
MMIR_API mmir_status MMIR_CALL mmir_cancellation_request(mmir_cancellation_handle cancellation);
MMIR_API mmir_status MMIR_CALL mmir_cancellation_release(mmir_cancellation_handle cancellation);
MMIR_API mmir_status MMIR_CALL mmir_render(
    mmir_engine_handle engine,
    const uint8_t* source_utf8,
    uint32_t source_length,
    const mmir_render_options* options,
    mmir_cancellation_handle cancellation,
    mmir_buffer_handle* out_buffer);
MMIR_API mmir_status MMIR_CALL mmir_buffer_data(
    mmir_buffer_handle buffer,
    const uint8_t** out_data,
    uint32_t* out_length);
MMIR_API mmir_status MMIR_CALL mmir_buffer_release(mmir_buffer_handle buffer);

#ifdef __cplusplus
}
#endif

#if defined(__cplusplus)
static_assert(sizeof(mmir_engine_options) == 24, "mmir_engine_options ABI size");
static_assert(sizeof(mmir_render_options) == 48, "mmir_render_options ABI size");
#else
_Static_assert(sizeof(mmir_engine_options) == 24, "mmir_engine_options ABI size");
_Static_assert(sizeof(mmir_render_options) == 48, "mmir_render_options ABI size");
#endif

#endif
