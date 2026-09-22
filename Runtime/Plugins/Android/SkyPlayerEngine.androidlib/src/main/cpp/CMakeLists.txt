cmake_minimum_required(VERSION 3.18)
project(exovideo)

add_library(exovideo SHARED exovideo.cpp)

find_library(log-lib log)
find_library(gles3-lib GLESv3)
find_library(egl-lib EGL)

target_link_libraries(exovideo ${log-lib} ${gles3-lib} ${egl-lib} android)
