cmake_minimum_required(VERSION 3.18)
project(exovideo)

add_library(skyplayerengine SHARED exovideo.cpp)

find_library(log-lib log)
find_library(gles3-lib GLESv3)
find_library(egl-lib EGL)

target_link_libraries(skyplayerengine ${log-lib} ${gles3-lib} ${egl-lib} android)
