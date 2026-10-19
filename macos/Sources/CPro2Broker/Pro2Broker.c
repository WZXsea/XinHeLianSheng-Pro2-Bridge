// Apache-2.0. Loading a caller-supplied, bundled shim keeps broker installation optional.
#include "Pro2Broker.h"
#include <dlfcn.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>


typedef uint32_t (*VersionFn)(void);
typedef int32_t (*ProbeFn)(Pro2BrokerStatus *);
typedef void *(*CreateFn)(int32_t,const char *,Pro2BrokerStatus *);
typedef int32_t (*SendFn)(void *,const Pro2BrokerInput *,Pro2BrokerStatus *);
typedef int32_t (*ReadFn)(void *,Pro2BrokerFeedback *);
typedef void (*DestroyFn)(void *);
struct Pro2BrokerHandle { void *library; void *device; ProbeFn probe; CreateFn create; SendFn send; ReadFn read; DestroyFn destroy; };
static void failure(Pro2BrokerStatus *status,int32_t code,const char *message) {
    if (!status) return;
    memset(status,0,sizeof(*status)); status->code = code;
    snprintf(status->message,sizeof(status->message),"%s",message ? message : "Broker load failure");
}
static Pro2BrokerRef load(const char *path,Pro2BrokerStatus *status) {
    if (!path || !*path) { failure(status,-1,"Optional libvirtualhid client shim is not installed"); return NULL; }
    void *library = dlopen(path,RTLD_NOW | RTLD_LOCAL);
    if (!library) { failure(status,-1,dlerror()); return NULL; }
    VersionFn version = (VersionFn)dlsym(library,"pro2_lvh_abi_version");
    if (!version || version() != PRO2_BROKER_ABI_VERSION) { failure(status,-2,"libvirtualhid client shim ABI mismatch"); dlclose(library); return NULL; }
    Pro2BrokerRef handle = calloc(1,sizeof(*handle));
    if (!handle) { failure(status,-3,"Could not allocate broker handle"); dlclose(library); return NULL; }
    handle->library = library;
    handle->probe = (ProbeFn)dlsym(library,"pro2_lvh_probe");
    handle->create = (CreateFn)dlsym(library,"pro2_lvh_create");
    handle->send = (SendFn)dlsym(library,"pro2_lvh_send");
    handle->read = (ReadFn)dlsym(library,"pro2_lvh_read_feedback");
    handle->destroy = (DestroyFn)dlsym(library,"pro2_lvh_destroy");
    if (!handle->probe || !handle->create || !handle->send || !handle->read || !handle->destroy) {
        failure(status,-2,"libvirtualhid client shim is missing required API functions"); dlclose(library); free(handle); return NULL;
    }
    return handle;
}
int32_t pro2_broker_client_self_test(const char *path,Pro2BrokerStatus *status) {
    Pro2BrokerRef handle = load(path,status);
    if (!handle) return status ? status->code : -1;
    ProbeFn run = (ProbeFn)dlsym(handle->library,"pro2_lvh_client_self_test");
    int32_t result = -2;
    if (run) result = run(status); else failure(status,-2,"Client test entry point is missing");
    dlclose(handle->library); free(handle); return result;
}
int32_t pro2_broker_probe(const char *path,Pro2BrokerStatus *status) {
    Pro2BrokerRef handle = load(path,status);
    if (!handle) return status ? status->code : -1;
    int32_t result = handle->probe(status); dlclose(handle->library); free(handle); return result;
}
Pro2BrokerRef pro2_broker_create(const char *path,int32_t profile,const char *stable_id,Pro2BrokerStatus *status) {
    Pro2BrokerRef handle = load(path,status);
    if (!handle) return NULL;
    handle->device = handle->create(profile,stable_id,status);
    if (!handle->device) { dlclose(handle->library); free(handle); return NULL; }
    return handle;
}
int32_t pro2_broker_send(Pro2BrokerRef handle,const Pro2BrokerInput *input,Pro2BrokerStatus *status) {
    if (!handle || !input) { failure(status,3,"Virtual gamepad is closed"); return 3; }
    return handle->send(handle->device,input,status);
}
int32_t pro2_broker_read_feedback(Pro2BrokerRef handle,Pro2BrokerFeedback *feedback) {
    return handle && feedback ? handle->read(handle->device,feedback) : 0;
}
void pro2_broker_destroy(Pro2BrokerRef handle) {
    if (handle) { handle->destroy(handle->device); dlclose(handle->library); free(handle); }
}
