# Distributed under the OSI-approved BSD 3-Clause License.  See accompanying
# file Copyright.txt or https://cmake.org/licensing for details.

cmake_minimum_required(VERSION ${CMAKE_VERSION}) # this file comes with cmake

# If CMAKE_DISABLE_SOURCE_CHANGES is set to true and the source directory is an
# existing directory in our source tree, calling file(MAKE_DIRECTORY) on it
# would cause a fatal error, even though it would be a no-op.
if(NOT EXISTS "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src")
  file(MAKE_DIRECTORY "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-src")
endif()
file(MAKE_DIRECTORY
  "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-build"
  "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix"
  "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix/tmp"
  "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix/src/soem-populate-stamp"
  "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix/src"
  "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix/src/soem-populate-stamp"
)

set(configSubDirs Debug)
foreach(subDir IN LISTS configSubDirs)
    file(MAKE_DIRECTORY "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix/src/soem-populate-stamp/${subDir}")
endforeach()
if(cfgdir)
  file(MAKE_DIRECTORY "C:/Users/ATI/source/repos/DirectControl/native/soemshim/build-win/_deps/soem-subbuild/soem-populate-prefix/src/soem-populate-stamp${cfgdir}") # cfgdir has leading slash
endif()
