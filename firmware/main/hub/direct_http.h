// HTTPS for direct mode: one connection per cloud service (Claude, speech to text, the voice),
// kept open between requests so a turn's sentences don't each pay for a TLS handshake.
#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string>
#include <vector>

namespace hub {
namespace direct {

struct Piece {
  const void* data;
  size_t len;
};

class Https {
 public:
  Https() = default;
  Https(const Https&) = delete;
  Https& operator=(const Https&) = delete;
  ~Https() { end(); }

  // POSTs the pieces as one body and reads the response's headers. Returns the HTTP status, or
  // -1 (err() says why). A kept-open connection the server had dropped is retried once, fresh.
  // headers: "Name: value" lines.
  int post(const std::string& url, const std::vector<std::string>& headers, const Piece* pieces, size_t n);
  // Some of the body: > 0 bytes, 0 at its end, -1 failed, -2 nothing arrived yet (read again)
  int read(char* buf, int len);
  // The rest of a small body (a transcription, an error), at most max bytes. False if it failed
  // or *cancel was set.
  bool readAll(std::string& out, size_t max, const volatile bool* cancel = nullptr);
  void close();  // drop the connection (after an error, or a body left unread)
  void end();    // and free everything
  bool idleFor(uint32_t ms) const;
  const char* err() const { return err_; }

 private:
  bool ensure(const std::string& url);
  int attempt(const std::vector<std::string>& headers, const Piece* pieces, size_t n);
  void* h_ = nullptr;  // esp_http_client_handle_t
  std::string host_;
  bool used_ = false;  // a request went out on the open connection
  uint32_t lastUse_ = 0;
  char err_[96] = "";
};

std::string hostOf(const std::string& url);

}  // namespace direct
}  // namespace hub
