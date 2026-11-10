// HTTPS for direct mode (see direct_http.h), on esp_http_client with the certificate bundle.
#include "direct_http.h"
#include "../platform.h"
#include <esp_crt_bundle.h>
#include <esp_http_client.h>
#include <stdio.h>
#include <string.h>
#include <algorithm>
#include "../config.h"

namespace hub {
namespace direct {

namespace {

constexpr int kTimeoutMs = 20000;     // connecting, sending, the first byte of the answer
constexpr int kPollMs = 120;          // reading a body: how long one read waits (keeps streams prompt)
constexpr const char* kAgent = "Pucksley/" PUCK_FW_VERSION;

std::string trimmed(const std::string& s) {
  size_t b = s.find_first_not_of(" \t");
  size_t e = s.find_last_not_of(" \t\r\n");
  return b == std::string::npos ? std::string() : s.substr(b, e - b + 1);
}

}  // namespace

std::string hostOf(const std::string& url) {
  size_t s = url.find("://");
  size_t b = s == std::string::npos ? 0 : s + 3;
  size_t e = url.find_first_of("/:?", b);
  return url.substr(b, e == std::string::npos ? std::string::npos : e - b);
}

bool Https::ensure(const std::string& url) {
  std::string host = hostOf(url);
  if (h_ && host != host_) end();
  if (!h_) {
    esp_http_client_config_t cfg = {};
    cfg.url = url.c_str();
    cfg.method = HTTP_METHOD_POST;
    cfg.timeout_ms = kTimeoutMs;
    cfg.buffer_size = 4096;
    cfg.buffer_size_tx = 2048;
    cfg.crt_bundle_attach = esp_crt_bundle_attach;
    cfg.keep_alive_enable = true;
    cfg.disable_auto_redirect = true;
    cfg.user_agent = kAgent;
    h_ = esp_http_client_init(&cfg);
    if (!h_) {
      snprintf(err_, sizeof err_, "Not enough memory for a connection");
      return false;
    }
    host_ = host;
    used_ = false;
  } else if (esp_http_client_set_url((esp_http_client_handle_t)h_, url.c_str()) != ESP_OK) {
    snprintf(err_, sizeof err_, "Bad address: %.60s", url.c_str());
    return false;
  }
  return true;
}

int Https::attempt(const std::vector<std::string>& headers, const Piece* pieces, size_t n) {
  auto h = (esp_http_client_handle_t)h_;
  esp_http_client_set_method(h, HTTP_METHOD_POST);
  esp_http_client_set_timeout_ms(h, kTimeoutMs);
  esp_http_client_delete_all_headers(h);
  esp_http_client_set_header(h, "User-Agent", kAgent);
  for (const std::string& line : headers) {
    size_t c = line.find(':');
    if (c == std::string::npos) continue;
    std::string k = trimmed(line.substr(0, c)), v = trimmed(line.substr(c + 1));
    if (!k.empty()) esp_http_client_set_header(h, k.c_str(), v.c_str());
  }
  size_t total = 0;
  for (size_t i = 0; i < n; i++) total += pieces[i].len;

  if (esp_http_client_open(h, (int)total) != ESP_OK) {
    snprintf(err_, sizeof err_, "Couldn't reach %.60s", host_.c_str());
    return -1;
  }
  used_ = true;
  for (size_t i = 0; i < n; i++) {
    const char* p = (const char*)pieces[i].data;
    size_t left = pieces[i].len;
    while (left > 0) {
      int w = esp_http_client_write(h, p, (int)std::min<size_t>(left, 8192));
      if (w <= 0) {
        snprintf(err_, sizeof err_, "Lost the connection to %.60s", host_.c_str());
        return -1;
      }
      p += w;
      left -= (size_t)w;
    }
  }
  if (esp_http_client_fetch_headers(h) < 0) {
    snprintf(err_, sizeof err_, "No answer from %.60s", host_.c_str());
    return -1;
  }
  int status = esp_http_client_get_status_code(h);
  esp_http_client_set_timeout_ms(h, kPollMs);
  lastUse_ = millis();
  return status;
}

int Https::post(const std::string& url, const std::vector<std::string>& headers, const Piece* pieces, size_t n) {
  err_[0] = 0;
  if (!ensure(url)) return -1;
  bool reused = used_;
  int status = attempt(headers, pieces, n);
  if (status < 0) {
    close();
    // a connection kept open since the last request may have been closed at the other end
    if (reused) status = attempt(headers, pieces, n);
    if (status < 0) close();
  }
  return status;
}

int Https::read(char* buf, int len) {
  if (!h_) return -1;
  auto h = (esp_http_client_handle_t)h_;
  int n = esp_http_client_read(h, buf, len);
  if (n > 0) {
    lastUse_ = millis();
    return n;
  }
  if (n == -ESP_ERR_HTTP_EAGAIN) return -2;
  if (n == 0) {
    if (esp_http_client_is_complete_data_received(h)) return 0;
    snprintf(err_, sizeof err_, "%.60s stopped answering", host_.c_str());
    return -1;
  }
  snprintf(err_, sizeof err_, "Lost the connection to %.60s", host_.c_str());
  return -1;
}

bool Https::readAll(std::string& out, size_t max, const volatile bool* cancel) {
  char buf[1024];
  uint32_t lastData = millis();
  for (;;) {
    if (cancel && *cancel) return false;
    int n = read(buf, sizeof buf);
    if (n > 0) {
      lastData = millis();
      size_t room = max > out.size() ? max - out.size() : 0;
      out.append(buf, std::min<size_t>(room, (size_t)n));
    } else if (n == 0) {
      return true;
    } else if (n == -2) {
      if (millis() - lastData > (uint32_t)kTimeoutMs) {
        snprintf(err_, sizeof err_, "%.60s took too long", host_.c_str());
        return false;
      }
    } else {
      return false;
    }
  }
}

void Https::close() {
  if (h_) esp_http_client_close((esp_http_client_handle_t)h_);
  used_ = false;
}

void Https::end() {
  if (h_) {
    esp_http_client_close((esp_http_client_handle_t)h_);
    esp_http_client_cleanup((esp_http_client_handle_t)h_);
  }
  h_ = nullptr;
  host_.clear();
  used_ = false;
}

bool Https::idleFor(uint32_t ms) const { return h_ && millis() - lastUse_ > ms; }

}  // namespace direct
}  // namespace hub
