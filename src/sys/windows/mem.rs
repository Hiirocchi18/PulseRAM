//! System memory: status, performance info, memory-list counters and the kernel purge commands.

use std::ffi::c_void;
use std::mem::{size_of, zeroed};
use std::ptr::{null, null_mut};

use windows_sys::Win32::System::Performance::{
    PdhAddEnglishCounterW, PdhCloseQuery, PdhCollectQueryData, PdhGetFormattedCounterValue, PdhOpenQueryW, PDH_FMT_COUNTERVALUE, PDH_FMT_LARGE,
    PDH_HCOUNTER, PDH_HQUERY,
};
use windows_sys::Win32::System::ProcessStatus::{K32GetPerformanceInfo, PERFORMANCE_INFORMATION};
use windows_sys::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};

use super::{nt, wide};
use crate::sys::{MemStatus, MemoryLists, PerfInfo};

pub fn memory_status() -> Option<MemStatus> {
    unsafe {
        let mut ms: MEMORYSTATUSEX = zeroed();
        ms.dwLength = size_of::<MEMORYSTATUSEX>() as u32;
        if GlobalMemoryStatusEx(&mut ms) == 0 {
            return None;
        }
        Some(MemStatus {
            memory_load: ms.dwMemoryLoad,
            total_phys: ms.ullTotalPhys,
            avail_phys: ms.ullAvailPhys,
            total_page_file: ms.ullTotalPageFile,
            avail_page_file: ms.ullAvailPageFile,
        })
    }
}

pub fn perf_info() -> Option<PerfInfo> {
    unsafe {
        let mut pi: PERFORMANCE_INFORMATION = zeroed();
        pi.cb = size_of::<PERFORMANCE_INFORMATION>() as u32;
        if K32GetPerformanceInfo(&mut pi, pi.cb) == 0 {
            return None;
        }
        Some(PerfInfo {
            commit_total: pi.CommitTotal as u64,
            commit_limit: pi.CommitLimit as u64,
            system_cache: pi.SystemCache as u64,
            kernel_total: pi.KernelTotal as u64,
            kernel_paged: pi.KernelPaged as u64,
            kernel_nonpaged: pi.KernelNonpaged as u64,
            page_size: if pi.PageSize > 0 { pi.PageSize as u64 } else { 4096 },
            process_count: pi.ProcessCount,
        })
    }
}

/// Reads the real Standby and Free/Zero page-list sizes from the built-in "Memory" performance counters.
/// These are readable without administrator rights.
pub struct MemCounters {
    query: PDH_HQUERY,
    standby: Vec<PDH_HCOUNTER>,
    free: PDH_HCOUNTER,
    available: bool,
}

// PDH handles are plain kernel-object references; the engine serialises access behind a mutex.
unsafe impl Send for MemCounters {}

impl MemCounters {
    pub fn new() -> Self {
        let mut c = MemCounters { query: null_mut(), standby: Vec::new(), free: null_mut(), available: false };
        unsafe {
            if PdhOpenQueryW(null(), 0, &mut c.query) != 0 {
                c.query = null_mut();
                return c;
            }
            for path in [
                r"\Memory\Standby Cache Core Bytes",
                r"\Memory\Standby Cache Normal Priority Bytes",
                r"\Memory\Standby Cache Reserve Bytes",
            ] {
                let mut counter: PDH_HCOUNTER = null_mut();
                if PdhAddEnglishCounterW(c.query, wide(path).as_ptr(), 0, &mut counter) == 0 {
                    c.standby.push(counter);
                }
            }
            if PdhAddEnglishCounterW(c.query, wide(r"\Memory\Free & Zero Page List Bytes").as_ptr(), 0, &mut c.free) != 0 {
                c.free = null_mut();
            }
            c.available = c.standby.len() == 3 && !c.free.is_null() && PdhCollectQueryData(c.query) == 0;
        }
        c
    }

    /// (standby bytes, free + zero bytes), or None when the counters can't be read.
    pub fn read(&mut self) -> Option<(f64, f64)> {
        if !self.available {
            return None;
        }
        unsafe {
            if PdhCollectQueryData(self.query) != 0 {
                return None;
            }
            let mut standby = 0.0;
            for &counter in &self.standby {
                standby += large_value(counter)? as f64;
            }
            let free = large_value(self.free)? as f64;
            Some((standby, free))
        }
    }
}

unsafe fn large_value(counter: PDH_HCOUNTER) -> Option<i64> {
    let mut value: PDH_FMT_COUNTERVALUE = zeroed();
    if PdhGetFormattedCounterValue(counter, PDH_FMT_LARGE, null_mut(), &mut value) != 0 {
        return None;
    }
    Some(value.Anonymous.largeValue)
}

impl Drop for MemCounters {
    fn drop(&mut self) {
        if !self.query.is_null() {
            unsafe { PdhCloseQuery(self.query) };
            self.query = null_mut();
        }
    }
}

const SYSTEM_MEMORY_LIST_INFORMATION_CLASS: i32 = 80;
const SYSTEM_COMBINE_PHYSICAL_MEMORY_INFORMATION_CLASS: i32 = 130;
const MEMORY_FLUSH_MODIFIED_LIST: i32 = 3;
const MEMORY_PURGE_STANDBY_LIST: i32 = 4;
const MEMORY_PURGE_LOW_PRIORITY_STANDBY_LIST: i32 = 5;

#[repr(C)]
struct SystemMemoryListInformation {
    zero_page_count: usize,
    free_page_count: usize,
    modified_page_count: usize,
    modified_no_write_page_count: usize,
    bad_page_count: usize,
    page_count_by_priority: [usize; 8],
    repurposed_pages_by_priority: [usize; 8],
    modified_page_count_page_file: usize,
}

/// Kernel memory lists (same data as System Informer's "Memory lists"): standby per priority and the
/// cumulative count of standby pages Windows has repurposed. Needs SeProfileSingleProcessPrivilege.
pub fn query_memory_lists() -> Option<MemoryLists> {
    unsafe {
        let mut info: SystemMemoryListInformation = zeroed();
        let mut returned = 0u32;
        let status = nt::NtQuerySystemInformation(
            SYSTEM_MEMORY_LIST_INFORMATION_CLASS,
            &mut info as *mut _ as *mut c_void,
            size_of::<SystemMemoryListInformation>() as u32,
            &mut returned,
        );
        if status != 0 {
            return None;
        }
        let mut lists = MemoryLists::default();
        for i in 0..8 {
            lists.standby_pages_by_priority[i] = info.page_count_by_priority[i] as u64;
            lists.repurposed_pages += info.repurposed_pages_by_priority[i] as u64;
        }
        Some(lists)
    }
}

fn memory_list_command(mut command: i32) -> bool {
    unsafe { nt::NtSetSystemInformation(SYSTEM_MEMORY_LIST_INFORMATION_CLASS, &mut command as *mut _ as *mut c_void, size_of::<i32>() as u32) == 0 }
}

/// Writes modified pages to disk so they can join the standby list.
pub fn flush_modified_list() -> bool {
    memory_list_command(MEMORY_FLUSH_MODIFIED_LIST)
}

/// Empties the standby cache: only the low-priority part, or all of it.
pub fn purge_standby(low_priority_only: bool) -> bool {
    memory_list_command(if low_priority_only { MEMORY_PURGE_LOW_PRIORITY_STANDBY_LIST } else { MEMORY_PURGE_STANDBY_LIST })
}

#[repr(C)]
struct MemoryCombineInformationEx {
    handle: *mut c_void,
    pages_combined: usize,
    flags: u64,
}

/// Asks the memory manager to merge identical pages. Returns the number of pages combined.
pub fn combine_pages() -> Option<u64> {
    unsafe {
        let mut info = MemoryCombineInformationEx { handle: null_mut(), pages_combined: 0, flags: 0 };
        let status = nt::NtSetSystemInformation(
            SYSTEM_COMBINE_PHYSICAL_MEMORY_INFORMATION_CLASS,
            &mut info as *mut _ as *mut c_void,
            size_of::<MemoryCombineInformationEx>() as u32,
        );
        (status == 0).then_some(info.pages_combined as u64)
    }
}
