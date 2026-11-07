# Builds the upstream C OpenJPEG shared library and opjdump, the reference sample dumper, into <build>/bin.
# Use build.ps1 rather than calling this directly.
cmake_minimum_required(VERSION 3.16)
project(openjpeg_reference C)

set(OPENJPEG_SOURCE_DIR "" CACHE PATH "Path to an uclouvain/openjpeg checkout")
if(NOT EXISTS "${OPENJPEG_SOURCE_DIR}/src/lib/openjp2/openjpeg.h")
  message(FATAL_ERROR "OPENJPEG_SOURCE_DIR is not an openjpeg checkout: '${OPENJPEG_SOURCE_DIR}'")
endif()

# Shared lib: opjdump uses it, and the native benchmarks P/Invoke it.
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
set(BUILD_STATIC_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_CODEC OFF CACHE BOOL "" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "" FORCE)
add_subdirectory("${OPENJPEG_SOURCE_DIR}" openjpeg EXCLUDE_FROM_ALL)

add_executable(opjdump opjdump.c)
target_link_libraries(opjdump PRIVATE openjp2)
target_include_directories(opjdump PRIVATE
  "${OPENJPEG_SOURCE_DIR}/src/lib/openjp2"
  "${CMAKE_BINARY_DIR}/openjpeg/src/lib/openjp2")
set_target_properties(opjdump PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
                                         RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin")
add_custom_command(TARGET opjdump POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:openjp2> "${CMAKE_BINARY_DIR}/bin")
