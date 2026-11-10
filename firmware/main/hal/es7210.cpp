// ES7210 microphone ADC: the puck's two MEMS mics, as I2S slave on the same bus as the ES8311.
//
// The register sequence is ported from Espressif's es7210 driver (Apache-2.0,
// SPDX-FileCopyrightText: 2015-2022 Espressif Systems (Shanghai) CO LTD), as used by esp-adf and
// the ESP32-S3 boards with this codec, reduced to one setup: slave, MCLK 256 fs at 16 kHz, 16-bit
// standard I2S, MIC1 on the left slot and MIC2 on the right.
#include "../platform.h"
#include "../config.h"
#include "hal_internal.h"

namespace hal {
namespace detail {

namespace {

constexpr uint8_t REG_RESET = 0x00;
constexpr uint8_t REG_CLOCK_OFF = 0x01;
constexpr uint8_t REG_MAINCLK = 0x02;
constexpr uint8_t REG_LRCK_DIVH = 0x04;
constexpr uint8_t REG_LRCK_DIVL = 0x05;
constexpr uint8_t REG_POWER_DOWN = 0x06;
constexpr uint8_t REG_OSR = 0x07;
constexpr uint8_t REG_MODE_CONFIG = 0x08;
constexpr uint8_t REG_TIME_CONTROL0 = 0x09;
constexpr uint8_t REG_TIME_CONTROL1 = 0x0A;
constexpr uint8_t REG_SDP_INTERFACE1 = 0x11;
constexpr uint8_t REG_SDP_INTERFACE2 = 0x12;
constexpr uint8_t REG_ADC34_HPF2 = 0x20;
constexpr uint8_t REG_ADC34_HPF1 = 0x21;
constexpr uint8_t REG_ADC12_HPF1 = 0x22;
constexpr uint8_t REG_ADC12_HPF2 = 0x23;
constexpr uint8_t REG_ANALOG = 0x40;
constexpr uint8_t REG_MIC12_BIAS = 0x41;
constexpr uint8_t REG_MIC34_BIAS = 0x42;
constexpr uint8_t REG_MIC1_GAIN = 0x43;
constexpr uint8_t REG_MIC2_GAIN = 0x44;
constexpr uint8_t REG_MIC3_GAIN = 0x45;
constexpr uint8_t REG_MIC4_GAIN = 0x46;
constexpr uint8_t REG_MIC1_POWER = 0x47;
constexpr uint8_t REG_MIC2_POWER = 0x48;
constexpr uint8_t REG_MIC3_POWER = 0x49;
constexpr uint8_t REG_MIC4_POWER = 0x4A;
constexpr uint8_t REG_MIC12_POWER = 0x4B;
constexpr uint8_t REG_MIC34_POWER = 0x4C;

bool s_present = false;

bool writeReg(uint8_t reg, uint8_t val) { return i2cWriteReg(I2C_ADDR_ES7210, reg, val); }

bool readReg(uint8_t reg, uint8_t* val) { return i2cReadReg(I2C_ADDR_ES7210, reg, val); }

bool updateBits(uint8_t reg, uint8_t mask, uint8_t data) {
  uint8_t v = 0;
  if (!readReg(reg, &v)) return false;
  v = (uint8_t)((v & ~mask) | (mask & data));
  return writeReg(reg, v);
}

// MIC1 and MIC2 on, 3 and 4 off (as es7210_mic_select)
bool selectMics() {
  bool ok = true;
  for (int i = 0; i < 4; i++) ok &= updateBits(REG_MIC1_GAIN + i, 0x10, 0x00);
  ok &= writeReg(REG_MIC12_POWER, 0xFF);
  ok &= writeReg(REG_MIC34_POWER, 0xFF);
  ok &= updateBits(REG_CLOCK_OFF, 0x0B, 0x00);
  ok &= writeReg(REG_MIC12_POWER, 0x00);
  ok &= updateBits(REG_MIC1_GAIN, 0x10, 0x10);
  ok &= updateBits(REG_MIC2_GAIN, 0x10, 0x10);
  return ok;
}

uint8_t gainCode(int db) {
  // 0..33 dB in 3 dB steps (codes 0..11), then 34.5 / 36 / 37.5 dB (12..14)
  if (db <= 0) return 0;
  if (db <= 33) return (uint8_t)(db / 3);
  if (db < 36) return 12;
  if (db < 37) return 13;
  return 14;
}

}  // namespace

bool es7210Begin(int gainDb) {
  I2cGuard lock;
  uint8_t probe = 0;
  if (!readReg(REG_RESET, &probe)) {
    hal::logLine("[hal] mic: ES7210 not found");
    return false;
  }
  bool ok = true;
  ok &= writeReg(REG_RESET, 0xFF);
  ok &= writeReg(REG_RESET, 0x41);
  ok &= writeReg(REG_CLOCK_OFF, 0x1F);
  ok &= writeReg(REG_TIME_CONTROL0, 0x30);   // chip state cycle
  ok &= writeReg(REG_TIME_CONTROL1, 0x30);   // power-on state cycle
  ok &= writeReg(REG_ADC12_HPF2, 0x2A);      // high-pass filters ("quick setup")
  ok &= writeReg(REG_ADC12_HPF1, 0x0A);
  ok &= writeReg(REG_ADC34_HPF2, 0x0A);
  ok &= writeReg(REG_ADC34_HPF1, 0x2A);
  ok &= updateBits(REG_MODE_CONFIG, 0x01, 0x00);  // slave: the ESP32 drives BCLK/LRCK
  ok &= writeReg(REG_ANALOG, 0xC3);          // analog power, VMID
  ok &= writeReg(REG_MIC12_BIAS, 0x70);      // mic bias 2.87 V
  ok &= writeReg(REG_MIC34_BIAS, 0x70);
  // clocks for MCLK 4.096 MHz, LRCK 16 kHz: adc_div 1, doubler on, dll on; OSR 0x20; LRCK div 256
  ok &= writeReg(REG_OSR, 0x20);
  ok &= writeReg(REG_MAINCLK, 0xC1);
  ok &= writeReg(REG_LRCK_DIVH, 0x01);
  ok &= writeReg(REG_LRCK_DIVL, 0x00);
  // serial port: 16-bit, standard I2S, not TDM (MIC1 left, MIC2 right on SDOUT1)
  uint8_t sdp = 0;
  readReg(REG_SDP_INTERFACE1, &sdp);
  sdp = (uint8_t)((sdp & 0x1C) | 0x60);
  ok &= writeReg(REG_SDP_INTERFACE1, sdp);
  ok &= writeReg(REG_SDP_INTERFACE2, 0x00);
  ok &= selectMics();
  uint8_t g = gainCode(gainDb);
  ok &= updateBits(REG_MIC1_GAIN, 0x0F, g);
  ok &= updateBits(REG_MIC2_GAIN, 0x0F, g);

  // start (as es7210_start, with the clock register as mic selection left it)
  uint8_t clk = 0x14;
  readReg(REG_CLOCK_OFF, &clk);
  ok &= writeReg(REG_CLOCK_OFF, clk);
  ok &= writeReg(REG_POWER_DOWN, 0x00);
  ok &= writeReg(REG_ANALOG, 0x43);
  ok &= writeReg(REG_MIC1_POWER, 0x08);
  ok &= writeReg(REG_MIC2_POWER, 0x08);
  ok &= writeReg(REG_MIC3_POWER, 0x08);
  ok &= writeReg(REG_MIC4_POWER, 0x08);
  ok &= selectMics();
  ok &= updateBits(REG_MIC1_GAIN, 0x0F, g);
  ok &= updateBits(REG_MIC2_GAIN, 0x0F, g);
  ok &= writeReg(REG_ANALOG, 0x43);
  ok &= writeReg(REG_RESET, 0x71);
  ok &= writeReg(REG_RESET, 0x41);
  if (!ok) hal::logLine("[hal] mic: ES7210 register writes failed");
  s_present = ok;
  return ok;
}

void es7210SetGain(int gainDb) {
  if (!s_present) return;
  I2cGuard lock;
  uint8_t g = gainCode(gainDb);
  updateBits(REG_MIC1_GAIN, 0x0F, g);
  updateBits(REG_MIC2_GAIN, 0x0F, g);
}

bool es7210Ok() { return s_present; }

}  // namespace detail
}  // namespace hal
