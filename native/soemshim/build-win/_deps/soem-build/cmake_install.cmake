# Install script for directory: C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src

# Set the install prefix
if(NOT DEFINED CMAKE_INSTALL_PREFIX)
  set(CMAKE_INSTALL_PREFIX "C:/Program Files/soemshim")
endif()
string(REGEX REPLACE "/$" "" CMAKE_INSTALL_PREFIX "${CMAKE_INSTALL_PREFIX}")

# Set the install configuration name.
if(NOT DEFINED CMAKE_INSTALL_CONFIG_NAME)
  if(BUILD_TYPE)
    string(REGEX REPLACE "^[^A-Za-z0-9_]+" ""
           CMAKE_INSTALL_CONFIG_NAME "${BUILD_TYPE}")
  else()
    set(CMAKE_INSTALL_CONFIG_NAME "Release")
  endif()
  message(STATUS "Install configuration: \"${CMAKE_INSTALL_CONFIG_NAME}\"")
endif()

# Set the component getting installed.
if(NOT CMAKE_INSTALL_COMPONENT)
  if(COMPONENT)
    message(STATUS "Install component: \"${COMPONENT}\"")
    set(CMAKE_INSTALL_COMPONENT "${COMPONENT}")
  else()
    set(CMAKE_INSTALL_COMPONENT)
  endif()
endif()

# Is this installation the result of a crosscompile?
if(NOT DEFINED CMAKE_CROSSCOMPILING)
  set(CMAKE_CROSSCOMPILING "FALSE")
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  if(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Dd][Ee][Bb][Uu][Gg])$")
    file(INSTALL DESTINATION "${CMAKE_INSTALL_PREFIX}/lib" TYPE STATIC_LIBRARY FILES "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/Debug/soem.lib")
  elseif(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Rr][Ee][Ll][Ee][Aa][Ss][Ee])$")
    file(INSTALL DESTINATION "${CMAKE_INSTALL_PREFIX}/lib" TYPE STATIC_LIBRARY FILES "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/Release/soem.lib")
  elseif(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Mm][Ii][Nn][Ss][Ii][Zz][Ee][Rr][Ee][Ll])$")
    file(INSTALL DESTINATION "${CMAKE_INSTALL_PREFIX}/lib" TYPE STATIC_LIBRARY FILES "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/MinSizeRel/soem.lib")
  elseif(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Rr][Ee][Ll][Ww][Ii][Tt][Hh][Dd][Ee][Bb][Ii][Nn][Ff][Oo])$")
    file(INSTALL DESTINATION "${CMAKE_INSTALL_PREFIX}/lib" TYPE STATIC_LIBRARY FILES "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/RelWithDebInfo/soem.lib")
  endif()
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  if(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Dd][Ee][Bb][Uu][Gg])$")
    include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/CMakeFiles/soem.dir/install-cxx-module-bmi-Debug.cmake" OPTIONAL)
  elseif(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Rr][Ee][Ll][Ee][Aa][Ss][Ee])$")
    include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/CMakeFiles/soem.dir/install-cxx-module-bmi-Release.cmake" OPTIONAL)
  elseif(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Mm][Ii][Nn][Ss][Ii][Zz][Ee][Rr][Ee][Ll])$")
    include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/CMakeFiles/soem.dir/install-cxx-module-bmi-MinSizeRel.cmake" OPTIONAL)
  elseif(CMAKE_INSTALL_CONFIG_NAME MATCHES "^([Rr][Ee][Ll][Ww][Ii][Tt][Hh][Dd][Ee][Bb][Ii][Nn][Ff][Oo])$")
    include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/CMakeFiles/soem.dir/install-cxx-module-bmi-RelWithDebInfo.cmake" OPTIONAL)
  endif()
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  file(INSTALL DESTINATION "${CMAKE_INSTALL_PREFIX}/include/soem" TYPE FILE FILES
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercat.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatbase.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatcoe.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatconfig.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatconfiglist.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatdc.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercateoe.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatfoe.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatmain.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatprint.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercatsoe.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/soem/ethercattype.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/osal/osal.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/osal/win32/inttypes.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/osal/win32/osal_defs.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/osal/win32/osal_win32.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/osal/win32/stdint.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/oshw/win32/nicdrv.h"
    "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src/oshw/win32/oshw.h"
    )
endif()

if(NOT CMAKE_INSTALL_LOCAL_ONLY)
  # Include the install script for each subdirectory.
  include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/test/linux/slaveinfo/cmake_install.cmake")
  include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/test/linux/eepromtool/cmake_install.cmake")
  include("C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/test/linux/simple_test/cmake_install.cmake")

endif()

string(REPLACE ";" "\n" CMAKE_INSTALL_MANIFEST_CONTENT
       "${CMAKE_INSTALL_MANIFEST_FILES}")
if(CMAKE_INSTALL_LOCAL_ONLY)
  file(WRITE "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build/install_local_manifest.txt"
     "${CMAKE_INSTALL_MANIFEST_CONTENT}")
endif()
