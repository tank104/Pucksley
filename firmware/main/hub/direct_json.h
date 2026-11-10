// A value deep inside a JSON answer, found by a dotted path the hub sends ("results.channels.0.alternatives.0.transcript").
// Header-only so the desktop tests can use it with ArduinoJson.
#pragma once

#include <ArduinoJson.h>

#include <stdlib.h>
#include <string>

namespace hub {
namespace direct {

// Each part is a property name, or a number that indexes an array. A plain name ("text") is the top-level property.
// Null (unset) when anything on the way is missing.
inline JsonVariantConst jsonAt(JsonVariantConst v, const std::string& path) {
  size_t start = 0;
  for (;;) {
    size_t dot = path.find('.', start);
    std::string part = path.substr(start, dot == std::string::npos ? std::string::npos : dot - start);
    bool index = !part.empty() && part.find_first_not_of("0123456789") == std::string::npos;
    if (index && v.is<JsonArrayConst>())
      v = v[(size_t)strtoul(part.c_str(), nullptr, 10)];
    else if (v.is<JsonObjectConst>())
      v = v[part];
    else
      return JsonVariantConst();
    if (v.isNull() || dot == std::string::npos) return v;
    start = dot + 1;
  }
}

}  // namespace direct
}  // namespace hub
