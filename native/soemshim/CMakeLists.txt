cmake_minimum_required(VERSION 3.16)
project(soemshim C)

set(CMAKE_C_STANDARD 99)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)   # SOEM 정적 라이브러리를 .so 에 링크하기 위해 필요
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Release)
endif()

# SOEM 1.4.0 을 소스로 받아 함께 빌드
# (SOEM 1.4.0 의 cmake_minimum_required 가 오래되어 CMake 4.x 에서 막히는 것을 방지)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
include(FetchContent)
# SOEM 1.4.0 은 BUILD_TESTS 를 강제로 TRUE 로 두고 link_directories 에 CMAKE_SOURCE_DIR 를 써서
# 하위 프로젝트로 넣으면 테스트 프로그램 링크가 깨진다 → EXCLUDE_FROM_ALL 로 soem 라이브러리만 빌드
if(CMAKE_VERSION VERSION_GREATER_EQUAL 3.28)
  FetchContent_Declare(
    soem
    GIT_REPOSITORY https://github.com/OpenEtherCATsociety/SOEM.git
    GIT_TAG        v1.4.0
    EXCLUDE_FROM_ALL
  )
  FetchContent_MakeAvailable(soem)
else()
  FetchContent_Declare(
    soem
    GIT_REPOSITORY https://github.com/OpenEtherCATsociety/SOEM.git
    GIT_TAG        v1.4.0
  )
  FetchContent_GetProperties(soem)
  if(NOT soem_POPULATED)
    FetchContent_Populate(soem)
    add_subdirectory(${soem_SOURCE_DIR} ${soem_BINARY_DIR} EXCLUDE_FROM_ALL)
  endif()
endif()

# SOEM 1.4.0 이 -Werror / /WX 를 켜 두어 최신 컴파일러에서 빌드가 막히는 것을 방지
if(TARGET soem)
  if(MSVC)
    target_compile_options(soem PRIVATE /WX- /wd4996)
  else()
    target_compile_options(soem PRIVATE -Wno-error)
  endif()
endif()

add_library(soemshim SHARED soemshim.c)

if(WIN32)
  set(SOEM_OS win32)
  target_include_directories(soemshim PRIVATE ${soem_SOURCE_DIR}/oshw/win32/wpcap/Include)
  target_link_directories(soemshim PRIVATE ${soem_SOURCE_DIR}/oshw/win32/wpcap/Lib/x64)
  target_link_libraries(soemshim PRIVATE soem wpcap Packet Ws2_32 Winmm)
  target_compile_options(soemshim PRIVATE /utf-8 /WX-)
else()
  set(SOEM_OS linux)
  target_link_libraries(soemshim PRIVATE soem pthread rt)
  set_target_properties(soemshim PROPERTIES C_VISIBILITY_PRESET hidden)
endif()

target_include_directories(soemshim PRIVATE
  ${soem_SOURCE_DIR}/soem
  ${soem_SOURCE_DIR}/osal
  ${soem_SOURCE_DIR}/osal/${SOEM_OS}
  ${soem_SOURCE_DIR}/oshw/${SOEM_OS})

install(TARGETS soemshim LIBRARY DESTINATION lib RUNTIME DESTINATION bin)
