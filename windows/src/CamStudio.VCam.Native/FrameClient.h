#pragma once
// Shared desktop protocol: BE u32 width + height, u8 status, tightly packed NV12.
class FrameClient {
    SOCKET socket_ = INVALID_SOCKET;
    ULONGLONG retry_ = 0;
    bool winsock_ = false;
    bool Transfer(char* p, size_t count, bool sendData) {
        while(count) {
            int n = sendData ? send(socket_, p, static_cast<int>(count), 0) : recv(socket_, p, static_cast<int>(count), 0);
            if(n <= 0) return false;
            p += n; count -= n;
        }
        return true;
    }
public:
    FrameClient() { WSADATA w; winsock_ = WSAStartup(MAKEWORD(2,2), &w)==0; }
    ~FrameClient() { Reset(); if(winsock_) WSACleanup(); }
    FrameClient(const FrameClient&) = delete;
    FrameClient& operator=(const FrameClient&) = delete;
    void Reset() { if(socket_!=INVALID_SOCKET) closesocket(socket_); socket_=INVALID_SOCKET; }
    bool Fill(BYTE* data, size_t size, UINT32 width, UINT32 height) {
        if(!winsock_) return false;
        if(socket_ == INVALID_SOCKET) {
            auto now=GetTickCount64(); if(now<retry_) return false; retry_=now+1000;
            socket_=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);
            if(socket_==INVALID_SOCKET) return false;
            DWORD timeout=500; BOOL yes=TRUE;
            setsockopt(socket_,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
            setsockopt(socket_,SOL_SOCKET,SO_SNDTIMEO,reinterpret_cast<char*>(&timeout),sizeof(timeout));
            setsockopt(socket_,IPPROTO_TCP,TCP_NODELAY,reinterpret_cast<char*>(&yes),sizeof(yes));
            sockaddr_in address{}; address.sin_family=AF_INET; address.sin_port=htons(47802); address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
            u_long nonblocking=1; ioctlsocket(socket_,FIONBIO,&nonblocking);
            int result=connect(socket_,reinterpret_cast<sockaddr*>(&address),sizeof(address));
            if(result!=0) {
                if(WSAGetLastError()!=WSAEWOULDBLOCK) { Reset(); return false; }
                fd_set writable, errors; FD_ZERO(&writable); FD_ZERO(&errors); FD_SET(socket_,&writable); FD_SET(socket_,&errors);
                timeval wait{0,200000};
                if(select(0,nullptr,&writable,&errors,&wait)<=0 || FD_ISSET(socket_,&errors)) { Reset(); return false; }
                int error=0, length=sizeof(error);
                if(getsockopt(socket_,SOL_SOCKET,SO_ERROR,reinterpret_cast<char*>(&error),&length)!=0 || error) { Reset(); return false; }
            }
            nonblocking=0; ioctlsocket(socket_,FIONBIO,&nonblocking);
        }
        UINT32 request[]={htonl(width),htonl(height)}; char status=0;
        if(!Transfer(reinterpret_cast<char*>(request),sizeof(request),true) || !Transfer(&status,1,false) || status!=1 || !Transfer(reinterpret_cast<char*>(data),size,false)) {
            Reset(); return false;
        }
        return true;
    }
};
