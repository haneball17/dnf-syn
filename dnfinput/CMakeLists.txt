cmake_minimum_required(VERSION 3.20)

project(dnfinput LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC AND NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "dnfinput 仅支持 x86/Win32，请使用 -A Win32 重新生成。")
endif()

add_subdirectory(third_party/minhook)

add_library(dnfinput SHARED
    src/dnfinput.cpp
)

target_include_directories(dnfinput PRIVATE
    third_party/minhook/include
)

target_compile_definitions(dnfinput PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    DIRECTINPUT_VERSION=0x0800
    _CRT_SECURE_NO_WARNINGS
)

if(MSVC)
    # 确保中文注释与字符串按 UTF-8 编译，避免源文件解析错误
    target_compile_options(dnfinput PRIVATE /W4 /utf-8)
endif()

target_link_libraries(dnfinput PRIVATE
    minhook
    user32
    dinput8
    dxguid
    ole32
)

set_target_properties(dnfinput PROPERTIES
    OUTPUT_NAME "dnfinput"
)
